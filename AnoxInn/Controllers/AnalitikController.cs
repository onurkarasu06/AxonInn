using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System;
using System.Net.Http; // Performans için eklendi
using AxonInn.Models.Entities;
using AxonInn.Models.Context;
using AxonInn.Models.Analitik;
using AxonInn.Models;

namespace AxonInn.Controllers
{
    public class AnalitikController : Controller
    {
        private readonly AxonInnContext _context;
        private readonly IConfiguration _configuration;

        // PERFORMANS (KRİTİK): Yüksek trafikli sayfalarda HttpClient her defasında new'lenirse
        // Sunucunun tüm Ağ (TCP) Portları tükenir ve çöker (Socket Exhaustion). Sınıf seviyesinde tekilleştirildi.
        private static readonly HttpClient _httpClient = new HttpClient();

        public AnalitikController(AxonInnContext context, IConfiguration configuration)
        {
            _context = context;
            _configuration = configuration;
        }

        private Personel? GetActiveUser()
        {
            // Request-Level Cache: Aynı sayfa yüklenmesinde aynı fonksiyonu defalarca çağırırsanız
            // JSON serileştirme döngüsü yorulmaz, RAM'den direkt çeker.
            if (HttpContext.Items["CachedUser"] is Personel cachedUser) return cachedUser;

            var personelJson = HttpContext.Session.GetString("GirisYapanPersonel");
            if (string.IsNullOrEmpty(personelJson)) return null;

            var user = System.Text.Json.JsonSerializer.Deserialize<Personel>(personelJson);
            HttpContext.Items["CachedUser"] = user;
            return user;
        }

        private Departman? GetSessionBilgisi(Personel loginOlanPersonel)
        {
            // Request-Level Cache: Veritabanına tekrar tekrar sorgu gitmesini engeller.
            if (HttpContext.Items["CachedSessionData"] is Departman cachedSession) return cachedSession;

            var sessionBilgisi = _context.Departmen.AsNoTracking()
                                     .Include(d => d.HotelRefNavigation)
                                     .FirstOrDefault(d => d.Id == loginOlanPersonel.DepartmanRef);

            HttpContext.Items["CachedSessionData"] = sessionBilgisi;
            return sessionBilgisi;
        }

        [Route("Analitik")]
        public async Task<IActionResult> Analitik(int? yil = null)
        {
            try
            {
                var loginOlanPersonel = GetActiveUser();
                if (loginOlanPersonel == null) return RedirectToAction("Login", "Login");

                var sessionBilgisi = GetSessionBilgisi(loginOlanPersonel);
                if (sessionBilgisi?.HotelRefNavigation == null) return RedirectToAction("Login", "Login");

                long aktifOtelId = sessionBilgisi.HotelRefNavigation.Id;
                ViewBag.HotelAdi = sessionBilgisi.HotelRefNavigation.Adi;

                var tumYorumlarQuery = _context.Yorum.AsNoTracking().Where(y => y.HotelRef == aktifOtelId);

                // DB VE RAM PERFORMANS: Eskiden veritabanındaki on binlerce metni RAM'e çekip
                // foreach ile "Regex" atıp yılları buluyordunuz. Veri arttıkça C#'ı kilitlerdi.
                // Bu kod doğrudan SQL'e "Git sadece yılları (YEAR) al, tekrar edenleri sil (DISTINCT) ve ver" der. 
                // Ağdan 10MB yerine 1KB veri geçer.
                var yillar = await tumYorumlarQuery
                   // Boş veya 4 karakterden kısa olan hatalı kayıtları filtreliyoruz
                   .Where(y => !string.IsNullOrEmpty(y.MisafirKonaklamaTarihi) && y.MisafirKonaklamaTarihi.Length >= 4)
                   // İlk 4 karakteri (YYYY kısmını) alıyoruz
                   .Select(y => y.MisafirKonaklamaTarihi.Substring(0, 4))
                   .Distinct()
                   .OrderByDescending(y => y)
                   .ToListAsync();

                ViewBag.Yillar = yillar;
                ViewBag.SeciliYil = yil;

                if (yil.HasValue && yil.Value > 0)
                {
                    tumYorumlarQuery = tumYorumlarQuery.Where(y => y.MisafirYorumTarihi != null && y.MisafirYorumTarihi.Value.Year == yil.Value);
                }

                List<Yorum> yorumList = await tumYorumlarQuery.ToListAsync();

                YorumDashboardGrafikServisi yorumDashboardGrafikServisi = new YorumDashboardGrafikServisi();
                YorumDashboardViewModel yorumDashboardViewModel = new YorumDashboardViewModel
                {
                    KpiVerileri = yorumDashboardGrafikServisi.HesaplaKpiKartlari(yorumList),
                    DuyguGrafik = yorumDashboardGrafikServisi.HesaplaDuyguPastaGrafigi(yorumList),
                    DepartmanGrafik = yorumDashboardGrafikServisi.HesaplaDepartmanBarGrafigi(yorumList),
                    HisPolarGrafik = yorumDashboardGrafikServisi.HesaplaHisPolarGrafigi(yorumList),
                    KelimeGrafik = yorumDashboardGrafikServisi.HesaplaKelimeBarGrafigi(yorumList),
                    UlkeGrafik = yorumDashboardGrafikServisi.HesaplaUlkeGrafigi(yorumList),
                    KonaklamaGrafik = yorumDashboardGrafikServisi.HesaplaKonaklamaTipiGrafigi(yorumList),
                    TrendGrafik = yorumDashboardGrafikServisi.HesaplaAylikTrendGrafigi(yorumList),
                    Yil = yil.HasValue ? yil.Value.ToString() : "Tüm Yıllar"
                };

                await LogKaydet(loginOlanPersonel, "Analitik Sayfasına Giriş Yapıldı", "Analitik Sayfası");
                return View(yorumDashboardViewModel);
            }
            catch (Exception)
            {
                return View("~/Views/Error/Error.cshtml", new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
            }
        }

        [HttpPost]
        public async Task<IActionResult> YapayZekaTavsiyesiAl([FromBody] YorumDashboardViewModel panelVerisi)
        {
            try
            {
                var loginOlanPersonel = GetActiveUser();

                if (loginOlanPersonel == null)
                    return RedirectToAction("Login", "Login");




                ////////////////////////////////////////////////////////////////////////////////////////////////////
                string jsonVeri = System.Text.Json.JsonSerializer.Serialize(panelVerisi);
                string prompt = "";
                string icindeBulunanYil = DateTime.Now.Year.ToString(); // Sistemden bulunduğumuz yılı alıyoruz

                // SADECE SEÇİLİ YIL "BU YIL" İSE KRİTİK YORUMLARI ÇEK VE DETAYLI PROMPT OLUŞTUR
                if (panelVerisi.Yil == icindeBulunanYil )
                {
                    var trendYorumlarListesi = await _context.Yorum
                                                 .Where(y => y.HotelRef == loginOlanPersonel.DepartmanRefNavigation.HotelRef)
                                                 .OrderByDescending(y => y.MisafirKonaklamaTarihi)
                                                 .Take(50) // Daha geniş ve doğru bir trend analizi için 50'ye çıkardık
                                                 .Select(y => new
                                                 {
                                                     Departman = y.GeminiAnalizIlgiliDepartman,
                                                     Duygu = y.GeminiAnalizDuyguDurumu
                                                 })
                                                 .ToListAsync();

                    // Çektiğimiz listeyi JSON formatına dönüştürüyoruz
                    string jsonKritikYorumlar = System.Text.Json.JsonSerializer.Serialize(trendYorumlarListesi);

                    prompt = $@"Sen AxonInn otel yönetim sistemi için çalışan kıdemli bir Turizm Stratejisti ve Veri Bilimcisisin.

                            Aşağıda otelimizin genel gidişatını gösteren 7 farklı analiz grafiğinin verilerini (GrafikVerileri) ve misafirlerin yaşadığı spesifik sorunları/durumları gösteren son kritik misafir yorumlarının özetlerini (KritikYorumlar) JSON olarak veriyorum:

                            GrafikVerileri:
                            {jsonVeri}

                            KritikYorumlar (Sorunların kök nedenini anlamak için bu gerçek verileri kullan):
                            {jsonKritikYorumlar}

                            Lütfen bu iki veri setini detaylıca sentezle. Sadece grafiklerdeki düşüşleri/çıkışları söyleme, 'KritikYorumlar' verisine bakarak bu trendlerin NEDEN yaşandığını tespit et ve otel müdürü için aksiyon alınabilir, net, profesyonel tavsiyeler üret.

                            ÇOK ÖNEMLİ KURALLAR:
                            1- Arayüzde yerimiz çok kısıtlı! Her bir tavsiye KESİNLİKLE EN FAZLA 6 CÜMLE olmalı. Lafı uzatma, tespit ettiğin spesifik sorunu söyle ve doğrudan çözüm öner.
                            2- Tavsiyelerini havada bırakma, mutlaka 'KritikYorumlar'da gördüğün gerçek misafir şikayetlerine veya beklentilerine dayandır.
                            3- SADECE aşağıdaki JSON formatında cevap ver. Markdown karakterleri (```json vb.) veya ekstra açıklamalar KESİNLİKLE KULLANMA.

                            İstenen JSON Kalıbı:
                            {{
                              ""DuyguTavsiyesi"": """",
                              ""DepartmanTavsiyesi"": """",
                              ""HisPolarTavsiyesi"": """",
                              ""KelimeTavsiyesi"": """",
                              ""UlkeTavsiyesi"": """",
                              ""KonaklamaTavsiyesi"": """",
                              ""TrendTavsiyesi"": """"
                            }}";
                }
                 
                                            // "TÜM YILLAR" VEYA "ESKİ YILLAR" SEÇİLİYSE YORUMLARI HİÇ ÇEKME VE SADECE GRAFİK PROMPTU VER
                                            else
                                            {
                                                prompt = $@"Sen AxonInn otel yönetim sistemi için çalışan kıdemli bir Turizm Stratejisti ve Veri Bilimcisisin.

                            Aşağıda otelimizin genel gidişatını gösteren 7 farklı analiz grafiğinin verilerini JSON olarak veriyorum:

                            GrafikVerileri:
                            {jsonVeri}

                            Lütfen bu verileri detaylıca incele ve otel müdürü için aksiyon alınabilir, net ve profesyonel tavsiyeler üret.

                            ÇOK ÖNEMLİ KURALLAR:
                            1- Arayüzde yerimiz çok kısıtlı! Her bir tavsiye KESİNLİKLE EN FAZLA 6 CÜMLE olmalı. Lafı uzatma, doğrudan sorunu söyle ve çözüm öner.
                            2- SADECE aşağıdaki JSON formatında cevap ver. Markdown karakterleri veya ekstra açıklamalar KULLANMA.

                            İstenen JSON Kalıbı:
                            {{
                              ""DuyguTavsiyesi"": """",
                              ""DepartmanTavsiyesi"": """",
                              ""HisPolarTavsiyesi"": """",
                              ""KelimeTavsiyesi"": """",
                              ""UlkeTavsiyesi"": """",
                              ""KonaklamaTavsiyesi"": """",
                              ""TrendTavsiyesi"": """"
                            }}";
                }
                ////////////////////////////////////////////////////////////////////////////////////////////////////



                string geminiApiKey = _configuration["GeminiApi:ApiKey"];
                string apiUrl = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key={geminiApiKey}";

                var requestData = new
                {
                    contents = new[] { new { parts = new[] { new { text = prompt } } } },
                    generationConfig = new { temperature = 0.2, response_mime_type = "application/json" }
                };

                var content = new System.Net.Http.StringContent(System.Text.Json.JsonSerializer.Serialize(requestData), System.Text.Encoding.UTF8, "application/json");

                // Statik Client'dan çağrı yapıyoruz (Tıkanmayı Engeller)
                var response = await _httpClient.PostAsync(apiUrl, content);

                 if (response.IsSuccessStatusCode)
                {
                    string jsonResponse = await response.Content.ReadAsStringAsync();
                    using (System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(jsonResponse))
                    {
                        var root = doc.RootElement;
                        var candidates = root.GetProperty("candidates");
                        if (candidates.GetArrayLength() > 0)
                        {
                            string aiCevabi = candidates[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? "";
                            aiCevabi = aiCevabi.Replace("```json", "").Replace("```", "").Trim();

                            var sonuc = System.Text.Json.JsonSerializer.Deserialize<AiTavsiyeSonucu>(aiCevabi, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                            await LogKaydet(loginOlanPersonel, "Analitik Sayfası YapayZekaTavsiyesiAl Yapıldı", aiCevabi);

                            return Json(sonuc);
                        }
                    }
                }
                return StatusCode(500, "Gemini API'den geçerli bir yanıt alınamadı. Lütfen daha sonra tekrar deneyin.");
            }
            catch (Exception ex)
            {
                return View("~/Views/Error/Error.cshtml", new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
            }
        }

        public async Task<IActionResult> TripadvisorYorumlariApifyApiKaydet()
        {
            var user = GetActiveUser();
            var session = user != null ? GetSessionBilgisi(user) : null;
            if (session?.HotelRefNavigation == null) return RedirectToAction("Login", "Login");

            long hotelID = session.HotelRefNavigation.Id;
            int getirilecekKayitAdeti = 600;
            string apiToken = _configuration["ApifyApiToken:ApiToken"];

            YorumIslem yorumIslem = new YorumIslem();
            List<Yorum> yorumList = await yorumIslem.TripadvisorYorumGetirApifyApiAsync(hotelID, getirilecekKayitAdeti, apiToken);
            yorumIslem.TripadvisorYorumKaydet(hotelID, yorumList, _context);

            return RedirectToAction("Yorum");
        }

        public async Task<IActionResult> TripadvisorYorumlariRapidApiKaydet()
        {
            var user = GetActiveUser();
            var session = user != null ? GetSessionBilgisi(user) : null;
            if (session?.HotelRefNavigation == null) return RedirectToAction("Login", "Login");

            long hotelID = session.HotelRefNavigation.Id;
            long tripadvisorHotelID = 23426767;
            int getirilecekKayitAdeti = 1000;
            string apiToken = _configuration["RapidApiToken:ApiToken"];

            YorumIslem yorumIslem = new YorumIslem();
            List<Yorum> yorumList = await yorumIslem.TripadvisorYorumGetirRapidApiAsync(hotelID, tripadvisorHotelID, getirilecekKayitAdeti, apiToken);
            yorumIslem.TripadvisorYorumKaydet(hotelID, yorumList, _context);

            return RedirectToAction("Yorum");
        }

        public async Task<IActionResult> GeminiAnalizleriKaydetAsync()
        {
            var user = GetActiveUser();
            var session = user != null ? GetSessionBilgisi(user) : null;
            if (session?.HotelRefNavigation == null) return RedirectToAction("Login", "Login");

            long hotelID = session.HotelRefNavigation.Id;
            string geminiApiKey = _configuration["GeminiApi:ApiKey"];

            YorumIslem yorumIslem = new YorumIslem();
            List<Yorum> dbYorumList = await yorumIslem.GeminiAnaliziOlmayanVeritabaniYorumListGetirAsync(hotelID, _context);

            if (dbYorumList == null || !dbYorumList.Any())
                return Ok("Analiz edilecek yeni yorum bulunamadı.");

            await yorumIslem.YorumlariPartilerHalindeIsleAsync(dbYorumList, yorumIslem, geminiApiKey, _context);

            return RedirectToAction("Yorum");
        }

        public async Task TripadvisordanAlamadigimizMisafirUlkesiniGeminiTahminEdipGuncellesinTopluAsync()
        {
            var user = GetActiveUser();
            if (user == null) return;
            var session = GetSessionBilgisi(user);
            if (session == null || session.HotelRefNavigation == null) return;

            string geminiApiKey = _configuration["GeminiApi:ApiKey"];
            string apiUrl = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key={geminiApiKey}";

            // PERFORMANS (Memory Leak Koruması): Veritabanındaki bütün yorumları aynı anda objeleriyle RAM'e çekip
            // ForEach ile "Tracker" (İzleyici) üzerinde tutmak OutOfMemory (Bellek yetersizliği) çöküşlerine neden olur.
            // Sadece Yorum ID'sini ve Ülke sütunlarını takipsiz (AsNoTracking) çekip gereksiz yükü sildik.
            var adayYorumlar = await _context.Yorum
                .AsNoTracking()
                .Where(y => y.HotelRef == session.HotelRefNavigation.Id && !string.IsNullOrEmpty(y.MisafirYorum))
                .Select(y => new { y.Id, y.MisafirUlkesi })
                .ToListAsync();

            var haricUlkeler = new HashSet<string> { "türkiye", "almanya", "rusya", "ingiltere", "kazakistan", "ukrayna" };

            // Veritabanı (Collation) harf duyarlılığı hatalarına girmemek adına Trim ve Lower RAM'de yapılır.
            var filtrelenmisIdler = adayYorumlar
                .Where(y => string.IsNullOrWhiteSpace(y.MisafirUlkesi) || !haricUlkeler.Contains(y.MisafirUlkesi.Trim().ToLower()))
                .Select(y => y.Id)
                .ToList();

            if (!filtrelenmisIdler.Any()) return;

            var gruplar = filtrelenmisIdler.Chunk(20).ToList();

            foreach (var idGrup in gruplar)
            {
                // Chunk (Dilimleme): Sadece işlem yapılacak 20 kayıt EF Core ChangeTracker izleyicisine alınır.
                var grupEntity = await _context.Yorum.Where(y => idGrup.Contains(y.Id)).ToListAsync();

                // GÜVENLİK (JSON Injection): Elle String.Replace() ile JSON oluşturmak, \n \r " \t gibi görünmez
                // karakterlerde API'nin patlamasına (Bad Request) sebep olur. Anonymous Type üzerinden güvenle geçirildi.
                var promptIcinYorumlar = grupEntity.Select((y, i) => new
                {
                    id = i,
                    mevcutUlke = string.IsNullOrWhiteSpace(y.MisafirUlkesi) ? "" : y.MisafirUlkesi.Trim(),
                    yorum = y.MisafirYorum
                }).ToList();

                // UnsafeRelaxedJsonEscaping kullanılarak Türkçe/Özel karakterlerin Unicode (\\u0000) yerine temiz metinle gitmesi sağlandı
                string jsonVeri = System.Text.Json.JsonSerializer.Serialize(promptIcinYorumlar, new System.Text.Json.JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });

                string prompt = $@"Sen uzman bir dilbilimci ve çevirmensin. Sana JSON formatında {grupEntity.Count} adet otel yorumu veriyorum.
Her bir kayıt için şu işlemi yap:
- Eğer 'mevcutUlke' BOŞ ise: 'yorum' metnindeki makine çevirisi hatalarını analiz edip misafirin ülkesini tahmin et ve TÜRKÇE yaz.
- Eğer 'mevcutUlke' DOLU ise: O kelimeyi sadece Türkçeye çevir (Örn: Germany -> Almanya).

Kesin Kurallar:
1. SADECE bir JSON dizisi (array) döndür.
2. Format kesinlikle şöyle olmalı: [{{""id"": 0, ""ulke"": ""Türkiye""}}]
3. Başında ve sonunda ```json gibi markdown işaretleri OLMASIN. Saf JSON ver.
4. Bulamazsan 'Bilinmiyor' yaz.

İşlenecek Veriler:
{jsonVeri}";

                try
                {
                    var requestData = new { contents = new[] { new { parts = new[] { new { text = prompt } } } }, generationConfig = new { temperature = 0.1 } };
                    var content = new System.Net.Http.StringContent(System.Text.Json.JsonSerializer.Serialize(requestData), System.Text.Encoding.UTF8, "application/json");

                    var response = await _httpClient.PostAsync(apiUrl, content);

                    if (response.IsSuccessStatusCode)
                    {
                        string jsonResponse = await response.Content.ReadAsStringAsync();
                        using (System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(jsonResponse))
                        {
                            var root = doc.RootElement;
                            var candidates = root.GetProperty("candidates");
                            if (candidates.GetArrayLength() > 0)
                            {
                                string aiCevabi = candidates[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? "";
                                aiCevabi = aiCevabi.Replace("```json", "").Replace("```", "").Trim();

                                if (!string.IsNullOrEmpty(aiCevabi))
                                {
                                    using (System.Text.Json.JsonDocument cevapDoc = System.Text.Json.JsonDocument.Parse(aiCevabi))
                                    {
                                        foreach (var eleman in cevapDoc.RootElement.EnumerateArray())
                                        {
                                            int arrayId = eleman.GetProperty("id").GetInt32();
                                            string bulunanUlke = eleman.GetProperty("ulke").GetString() ?? "";

                                            if (!string.IsNullOrWhiteSpace(bulunanUlke) && bulunanUlke != "Bilinmiyor" && arrayId < grupEntity.Count)
                                            {
                                                grupEntity[arrayId].MisafirUlkesi = bulunanUlke;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    // Herhangi bir Rate Limit (Kotalama) veya Timeout hatasında sistemi patlatmadan döngü diğer 20'li gruba geçer.
                }

                await _context.SaveChangesAsync();

                // EF CORE RAM TEMİZLİĞİ: Bu kod on binlerce yorumun sunucuyu şişirmesini kesin olarak durdurur. 
                // Yapılan işlemler Update edildikten sonra izleme mekanizması RAM'den boşaltılır.
                _context.ChangeTracker.Clear();
            }
        }

        private async Task<bool> LogKaydet(Personel? personel, string islemTipi, string yeniDeger)
        {
            try
            {
                string departmanAdi = "";
                string hotelAdi = "";

                if (personel != null && personel.DepartmanRef != 0)
                {
                    var depBilgisi = await _context.Departmen
                        .AsNoTracking()
                        .Where(d => d.Id == personel.DepartmanRef)
                        .Select(d => new { d.Adi, HotelAdi = d.HotelRefNavigation != null ? d.HotelRefNavigation.Adi : "" })
                        .FirstOrDefaultAsync();

                    if (depBilgisi != null)
                    {
                        departmanAdi = departmanAdi;
                        hotelAdi = depBilgisi.HotelAdi;
                    }
                }

                var log = new AuditLog
                {
                    IslemTarihi = DateTime.Now,
                    IlgiliTablo = "SayfaZiyareti",
                    KayitRefId = personel?.Id ?? 0,
                    IslemTipi = islemTipi,
                    EskiDeger = "",
                    YeniDeger = yeniDeger,
                    YapanHotelAd = hotelAdi,
                    YapanDepartmanAd = departmanAdi,
                    YapanAdSoyad = personel != null ? $"{personel.Adi} {personel.Soyadi}" : "Bilinmeyen"
                };

                _context.AuditLogs.Add(log);
                await _context.SaveChangesAsync();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}