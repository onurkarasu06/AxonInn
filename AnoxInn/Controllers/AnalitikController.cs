using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System;
using System.Net.Http;
using System.Net.Http.Json; // Performans optimizasyonu için eklendi
using System.Text.Json;
using System.Text.Json.Serialization;
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

        // PERFORMANS: Ağ (TCP) Portları tükenmesine karşı tekilleştirildi
        private static readonly HttpClient _httpClient = new HttpClient();

        // PERFORMANS: JSON Objeleri sürekli RAM'de yaratılıp silinmemesi için Static olarak önbelleklendi
        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        private static readonly JsonSerializerOptions _jsonRelaxedOptions = new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public AnalitikController(AxonInnContext context, IConfiguration configuration)
        {
            _context = context;
            _configuration = configuration;
        }

        private Personel? GetActiveUser()
        {
            if (HttpContext.Items["CachedUser"] is Personel cachedUser) return cachedUser;

            var personelJson = HttpContext.Session.GetString("GirisYapanPersonel");
            if (string.IsNullOrEmpty(personelJson)) return null;

            var user = JsonSerializer.Deserialize<Personel>(personelJson, _jsonOptions);
            HttpContext.Items["CachedUser"] = user;
            return user;
        }

        private Departman? GetSessionBilgisi(Personel loginOlanPersonel)
        {
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

                var yillar = await tumYorumlarQuery
                   .Where(y => !string.IsNullOrEmpty(y.MisafirKonaklamaTarihi) && y.MisafirKonaklamaTarihi.Length >= 4)
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

                string jsonVeri = JsonSerializer.Serialize(panelVerisi, _jsonOptions);
                string prompt = "";
                string icindeBulunanYil = DateTime.Now.Year.ToString();

                if (panelVerisi.Yil == icindeBulunanYil)
                {
                    // PERFORMANS: Read-Only (Okunabilir) veri olduğu için EF Core'da AsNoTracking() belleği korur
                    var trendYorumlarListesi = await _context.Yorum
                                                 .AsNoTracking()
                                                 .Where(y => y.HotelRef == loginOlanPersonel.DepartmanRefNavigation.HotelRef)
                                                 .OrderByDescending(y => y.MisafirKonaklamaTarihi)
                                                 .Take(50)
                                                 .Select(y => new
                                                 {
                                                     Departman = y.GeminiAnalizIlgiliDepartman,
                                                     Duygu = y.GeminiAnalizDuyguDurumu
                                                 })
                                                 .ToListAsync();

                    string jsonKritikYorumlar = JsonSerializer.Serialize(trendYorumlarListesi, _jsonOptions);

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

                string geminiApiKey = _configuration["GeminiApi:ApiKey"];
                string apiUrl = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key={geminiApiKey}";
                var requestData = new
                {
                    contents = new[] { new { parts = new[] { new { text = prompt } } } },
                    generationConfig = new { temperature = 0.2, response_mime_type = "application/json" }
                };

                // PERFORMANS: StringContent ile devasa metinler oluşturup RAM şişirmek yerine PostAsJsonAsync ile doğrudan stream akışına yazılır.
                var response = await _httpClient.PostAsJsonAsync(apiUrl, requestData, _jsonOptions);

                if (response.IsSuccessStatusCode)
                {
                    string jsonResponse = await response.Content.ReadAsStringAsync();
                    using (JsonDocument doc = JsonDocument.Parse(jsonResponse))
                    {
                        var root = doc.RootElement;
                        var candidates = root.GetProperty("candidates");
                        if (candidates.GetArrayLength() > 0)
                        {
                            string aiCevabi = candidates[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? "";
                            aiCevabi = aiCevabi.Replace("```json", "").Replace("```", "").Trim();

                            var sonuc = JsonSerializer.Deserialize<AiTavsiyeSonucu>(aiCevabi, _jsonOptions);

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

            // PERFORMANS: İşlem asenkron yapılarak thread (I/O) kilitlenmesi önlendi
            await yorumIslem.TripadvisorYorumKaydetAsync(hotelID, yorumList, _context);

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

            // PERFORMANS: Asenkron işlem
            await yorumIslem.TripadvisorYorumKaydetAsync(hotelID, yorumList, _context);

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

            if (dbYorumList == null || dbYorumList.Count == 0)
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

            var adayYorumlar = await _context.Yorum
                .AsNoTracking()
                .Where(y => y.HotelRef == session.HotelRefNavigation.Id && !string.IsNullOrEmpty(y.MisafirYorum))
                .Select(y => new { y.Id, y.MisafirUlkesi })
                .ToListAsync();

            // CPU OPTİMİZASYONU: "ToLower()" kullanmak her satırda hafızada geçici string üretir.
            // StringComparer.OrdinalIgnoreCase kullanılarak en yüksek doğruluk ve hız elde edildi.
            var haricUlkeler = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "türkiye", "almanya", "rusya", "ingiltere", "kazakistan", "ukrayna" };

            var filtrelenmisIdler = adayYorumlar
                .Where(y => string.IsNullOrWhiteSpace(y.MisafirUlkesi) || !haricUlkeler.Contains(y.MisafirUlkesi.Trim()))
                .Select(y => y.Id)
                .ToList();

            if (filtrelenmisIdler.Count == 0) return;

            var gruplar = filtrelenmisIdler.Chunk(20).ToList();

            foreach (var idGrup in gruplar)
            {
                var grupEntity = await _context.Yorum.Where(y => idGrup.Contains(y.Id)).ToListAsync();

                var promptIcinYorumlar = grupEntity.Select((y, i) => new
                {
                    id = i,
                    mevcutUlke = string.IsNullOrWhiteSpace(y.MisafirUlkesi) ? "" : y.MisafirUlkesi.Trim(),
                    yorum = y.MisafirYorum
                }).ToList();

                string jsonVeri = JsonSerializer.Serialize(promptIcinYorumlar, _jsonRelaxedOptions);

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

                    var response = await _httpClient.PostAsJsonAsync(apiUrl, requestData, _jsonOptions);

                    if (response.IsSuccessStatusCode)
                    {
                        string jsonResponse = await response.Content.ReadAsStringAsync();
                        using (JsonDocument doc = JsonDocument.Parse(jsonResponse))
                        {
                            var root = doc.RootElement;
                            var candidates = root.GetProperty("candidates");
                            if (candidates.GetArrayLength() > 0)
                            {
                                string aiCevabi = candidates[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? "";
                                aiCevabi = aiCevabi.Replace("```json", "").Replace("```", "").Trim();

                                if (!string.IsNullOrEmpty(aiCevabi))
                                {
                                    using (JsonDocument cevapDoc = JsonDocument.Parse(aiCevabi))
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
                    // İstisna durumlarda patlamadan sıradaki bloğa geçecek.
                }

                await _context.SaveChangesAsync();
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
                        // BUG FIX: Mevcut kodda yer alan departmanAdi = departmanAdi ataması düzeltildi.
                        departmanAdi = depBilgisi.Adi;
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

                await _context.AuditLogs.AddAsync(log); // Add yerine AddAsync
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