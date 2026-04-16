using AxonInn.Models;
using AxonInn.Models.Analitik;
using AxonInn.Models.Context;
using AxonInn.Models.Entities;
using AxonInn.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace AxonInn.Controllers
{
    [AutoValidateAntiforgeryToken]
    public class AnalitikController : Controller
    {
        private readonly AxonInnContext _context;
        private readonly IConfiguration _configuration;
        private readonly ILogService _logService;
        private readonly ICurrentUserService _currentUserService;

        private static readonly HttpClient _httpClient = new HttpClient();

        // 🛠️ DÜZELTME: Sonsuz döngüleri engelleyen standart ReferenceHandler eklendi
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReferenceHandler = ReferenceHandler.IgnoreCycles,
            PropertyNameCaseInsensitive = true // ⚡ EKLENDİ: Gelen JSON'daki büyük/küçük harf uyuşmazlığını tolere eder
        };

        private static readonly JsonSerializerOptions _jsonRelaxedOptions = new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        // 🛠️ HATA 1 DÜZELTİLDİ: Tüm Dependency Injection servisleri tek bir constructorda birleştirildi.
        public AnalitikController(AxonInnContext context, IConfiguration configuration, ILogService logService, ICurrentUserService currentUserService)
        {
            _context = context;
            _configuration = configuration;
            _logService = logService;
            _currentUserService = currentUserService;
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
        public async Task<IActionResult> Analitik(string? ay = null) // int? yil yerine string? ay kullanıyoruz
        {
            try
            {
                var loginOlanPersonel = _currentUserService.GetUser();

                if (loginOlanPersonel == null)
                {
                    HttpContext.Session.Remove("GirisYapanPersonel");
                    return RedirectToAction("Login", "Login");
                }

                var sessionBilgisi = GetSessionBilgisi(loginOlanPersonel);
                if (sessionBilgisi?.HotelRefNavigation == null) return RedirectToAction("Login", "Login");

                long aktifOtelId = sessionBilgisi.HotelRefNavigation.Id;
                ViewBag.HotelAdi = sessionBilgisi.HotelRefNavigation.Adi;

                var tumYorumlarQuery = _context.Yorum.AsNoTracking().Where(y => y.HotelRef == aktifOtelId);

                // YYYY-MM formatında (Örn: 2024-05) benzersiz ayları çekiyoruz
                var aylar = await tumYorumlarQuery
                   .Where(y => !string.IsNullOrEmpty(y.MisafirKonaklamaTarihi) && y.MisafirKonaklamaTarihi.Length >= 7)
                   .Select(y => y.MisafirKonaklamaTarihi.Substring(0, 7))
                   .Distinct()
                   .OrderByDescending(a => a)
                   .ToListAsync();

                ViewBag.Aylar = aylar;
                ViewBag.SeciliAy = ay;

                // Seçili ay varsa filtrelemeyi MisafirKonaklamaTarihi üzerinden yapıyoruz
                if (!string.IsNullOrEmpty(ay))
                {
                    tumYorumlarQuery = tumYorumlarQuery.Where(y => !string.IsNullOrEmpty(y.MisafirKonaklamaTarihi) && y.MisafirKonaklamaTarihi.StartsWith(ay));
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
                    // Modeldeki prop adını değiştirmemek için veriyi Yil parametresinde taşıyoruz
                    Yil = !string.IsNullOrEmpty(ay) ? ay : "Tüm Zamanlar"
                };

                await _logService.LogKaydetAsync(loginOlanPersonel, "Analitik Sayfasına Giriş Yapıldı", string.Empty, "Analitik Sayfası Görüntüleme", ViewBag.HotelAdi ?? string.Empty, string.Empty);

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
                // 1. KONTROL: Tüm zamanlar verisi üzerinden analiz yapılmasını engelleyelim
                if (string.IsNullOrEmpty(panelVerisi.Yil) || panelVerisi.Yil == "Tüm Zamanlar")
                {
                    // Frontend tarafındaki fetch(ok) kontrolüne takılması için BadRequest dönüyoruz
                    return BadRequest("Yapay zeka analizinin sağlıklı çalışabilmesi için lütfen belirli bir ay seçiniz. 'Tüm Zamanlar' verisi üzerinden özet oluşturulamamaktadır.");
                }

                var loginOlanPersonel = _currentUserService.GetUser();

                if (loginOlanPersonel == null)
                    return RedirectToAction("Login", "Login");

                string jsonVeri = JsonSerializer.Serialize(panelVerisi, _jsonOptions);
                string prompt = "";

                // YYYY-MM formatında mevcut ayı alıyoruz (Örn: 2024-05)
                string icindeBulunanAy = DateTime.Now.ToString("yyyy-MM");

                // panelVerisi.Yil propertysi artık string olarak "2024-05" gibi ayları veya "Tüm Zamanlar" tutuyor
                if (panelVerisi.Yil == icindeBulunanAy)
                {
                    var trendYorumlarListesi = await _context.Yorum
                                                 .AsNoTracking()
                                                 .Where(y => y.HotelRef == loginOlanPersonel.DepartmanRefNavigation.HotelRef)
                                                 .OrderByDescending(y => y.MisafirKonaklamaTarihi)
                                                 .Take(50) // Son 50 güncel yorumu kök neden analizi için çekiyoruz
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

                // Hatalı markdown formatı temizlendi, saf URL bırakıldı
                string apiUrl = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key={geminiApiKey}";

                var requestData = new
                {
                    contents = new[] { new { parts = new[] { new { text = prompt } } } },
                    generationConfig = new { temperature = 0.2, response_mime_type = "application/json" }
                };

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

                            await _logService.LogKaydetAsync(loginOlanPersonel, "Yapay Zeka Tavsiyesi Alındı", string.Empty, aiCevabi);

                            return Json(sonuc);
                        }
                    }
                }
                return StatusCode(500, "Gemini API'den geçerli bir yanıt alınamadı. Lütfen daha sonra tekrar deneyin.");
            }
            catch (Exception)
            {
                return View("~/Views/Error/Error.cshtml", new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
            }
        }

        public async Task<IActionResult> TripadvisorYorumlariApifyApiKaydet()
        {
            var user = _currentUserService.GetUser();
            var session = user != null ? GetSessionBilgisi(user) : null;
            if (session?.HotelRefNavigation == null) return RedirectToAction("Login", "Login");

            long hotelID = session.HotelRefNavigation.Id;
            int getirilecekKayitAdeti = 600;
            string apiToken = _configuration["ApifyApiToken:ApiToken"];

            YorumIslem yorumIslem = new YorumIslem();
            List<Yorum> yorumList = await yorumIslem.TripadvisorYorumGetirApifyApiAsync(hotelID, getirilecekKayitAdeti, apiToken);

            await yorumIslem.TripadvisorYorumKaydetAsync(hotelID, yorumList, _context);

            return RedirectToAction("Yorum");
        }

        public async Task<IActionResult> TripadvisorYorumlariRapidApiKaydet()
        {
            var user =   _currentUserService.GetUser();
            var session = user != null ? GetSessionBilgisi(user) : null;
            if (session?.HotelRefNavigation == null) return RedirectToAction("Login", "Login");

            long hotelID = session.HotelRefNavigation.Id;
            long tripadvisorHotelID = 23426767;
            int getirilecekKayitAdeti = 1000;
            string apiToken = _configuration["RapidApiToken:ApiToken"];

            YorumIslem yorumIslem = new YorumIslem();
            List<Yorum> yorumList = await yorumIslem.TripadvisorYorumGetirRapidApiAsync(hotelID, tripadvisorHotelID, getirilecekKayitAdeti, apiToken);

            await yorumIslem.TripadvisorYorumKaydetAsync(hotelID, yorumList, _context);

            return RedirectToAction("Yorum");
        }

        public async Task<IActionResult> GeminiAnalizleriKaydetAsync()
        {
            var user = _currentUserService.GetUser();
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
            var user = _currentUserService.GetUser();
            if (user == null) return;
            var session = GetSessionBilgisi(user);
            if (session == null || session.HotelRefNavigation == null) return;

            string geminiApiKey = _configuration["GeminiApi:ApiKey"];
            string apiUrl = $"[https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key=](https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key=){geminiApiKey}";

            var adayYorumlar = await _context.Yorum
                .AsNoTracking()
                .Where(y => y.HotelRef == session.HotelRefNavigation.Id && !string.IsNullOrEmpty(y.MisafirYorum))
                .Select(y => new { y.Id, y.MisafirUlkesi })
                .ToListAsync();

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

                }

                await _context.SaveChangesAsync();
                _context.ChangeTracker.Clear();
            }
        }
    }
}