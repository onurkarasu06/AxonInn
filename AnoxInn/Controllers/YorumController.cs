using AxonInn.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System;

namespace AxonInn.Controllers
{
    public class YorumController : Controller
    {
        private readonly AxonInnContext _context;
        private readonly IConfiguration _configuration;

        public YorumController(AxonInnContext context, IConfiguration configuration)
        {
            _context = context;
            _configuration = configuration;
        }

        private Personel? GetActiveUser()
        {
            var personelJson = HttpContext.Session.GetString("GirisYapanPersonel");
            if (string.IsNullOrEmpty(personelJson))
                return null;
            return System.Text.Json.JsonSerializer.Deserialize<Personel>(personelJson);
        }

        private Departman? GetSessionBilgisi(Personel loginOlanPersonel)
        {
            return _context.Departmen.AsNoTracking()
                                     .Include(d => d.HotelRefNavigation)
                                     .FirstOrDefault(d => d.Id == loginOlanPersonel.DepartmanRef);
        }

        public async Task<IActionResult> Yorum(int? yil = null)
        {
            try
            {
                var loginOlanPersonel = GetActiveUser();
                if (loginOlanPersonel == null) return RedirectToAction("Login", "Login");

                var sessionBilgisi = GetSessionBilgisi(loginOlanPersonel);
                if (sessionBilgisi?.HotelRefNavigation == null) return RedirectToAction("Login", "Login");

                long aktifOtelId = sessionBilgisi.HotelRefNavigation.Id;
                ViewBag.HotelAdi = sessionBilgisi.HotelRefNavigation.Adi;

                // 1. Önce otele ait TÜM yorumları çekiyoruz
                var tumYorumlarQuery = _context.Yorum.AsNoTracking().Where(y => y.HotelRef == aktifOtelId);

                // 2. Veritabanındaki benzersiz (farklı) yılları bulup Combo Box için hazırlıyoruz
                var yillar = await tumYorumlarQuery
                    .Where(y => y.MisafirYorumTarihi != null)
                    .Select(y => y.MisafirYorumTarihi.Value.Year)
                    .Distinct()
                    .OrderByDescending(y => y)
                    .ToListAsync();

                ViewBag.Yillar = yillar;
                ViewBag.SeciliYil = yil;

                // 3. EĞER KULLANICI BİR YIL SEÇTİYSE SADECE O YILIN VERİLERİNİ FİLTRELE
                if (yil.HasValue && yil.Value > 0)
                {
                    tumYorumlarQuery = tumYorumlarQuery.Where(y => y.MisafirYorumTarihi != null && y.MisafirYorumTarihi.Value.Year == yil.Value);
                }

                // Listeyi hafızaya alıyoruz
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
                    TrendGrafik = yorumDashboardGrafikServisi.HesaplaAylikTrendGrafigi(yorumList)
                };

                return View(yorumDashboardViewModel);
            }
            catch (Exception)
            {
                // 🛡️ GÜVENLİK: Information Disclosure (Tablo/Ağaç sızıntısı) önlemi
                return View("~/Views/Error/Error.cshtml", new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
            }
        }

        public async Task<IActionResult> TripadvisorYorumlariApifyApiKaydet()
        {
            var user = GetActiveUser();
            var session = user != null ? GetSessionBilgisi(user) : null;
            if (session?.HotelRefNavigation == null) return RedirectToAction("Login", "Login");

            // Hardcoded "hotelID = 1" hatası düzeltildi! Dinamik Session ID atandı.
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

        [HttpPost]
        public async Task<IActionResult> YapayZekaTavsiyesiAl([FromBody] YorumDashboardViewModel panelVerisi)
        {
            try
            {
                string jsonVeri = System.Text.Json.JsonSerializer.Serialize(panelVerisi);
                string prompt = $@"Sen AxonInn otel yönetim sistemi için çalışan kıdemli bir Turizm Stratejisti ve Veri Bilimcisisin. 
                                Aşağıda otelimizin güncel 7 farklı analiz grafiğinin verilerini JSON olarak veriyorum:
                                {jsonVeri}

                                Lütfen bu verileri detaylıca incele ve otel müdürü için aksiyon alınabilir, net ve profesyonel tavsiyeler üret.
                                ÇOK ÖNEMLİ KURALLAR:
                                1- Arayüzde yerimiz çok kısıtlı! Her bir tavsiye KESİNLİKLE EN FAZLA 8 KISA CÜMLE olmalı. Lafı uzatma, doğrudan sorunu söyle ve çözüm öner.
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

                string geminiApiKey = _configuration["GeminiApi:ApiKey"];

                // KRİTİK HATA DÜZELTİLDİ: İçine giren "[https://...](https://...)" Markdown String yapısı saf URL'e dönüştürüldü.
                string apiUrl = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key={geminiApiKey}";

                using (var httpClient = new System.Net.Http.HttpClient())
                {
                    var requestData = new
                    {
                        contents = new[] { new { parts = new[] { new { text = prompt } } } },
                        generationConfig = new { temperature = 0.2, response_mime_type = "application/json" }
                    };

                    var content = new System.Net.Http.StringContent(System.Text.Json.JsonSerializer.Serialize(requestData), System.Text.Encoding.UTF8, "application/json");
                    var response = await httpClient.PostAsync(apiUrl, content);

                    if (response.IsSuccessStatusCode)
                    {
                        string jsonResponse = await response.Content.ReadAsStringAsync();
                        using (System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(jsonResponse))
                        {
                            var root = doc.RootElement;
                            var candidates = root.GetProperty("candidates");
                            if (candidates.GetArrayLength() > 0)
                            {
                                string aiCevabi = candidates[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();
                                aiCevabi = aiCevabi.Replace("```json", "").Replace("```", "").Trim();

                                // Güvenli Parse
                                var sonuc = System.Text.Json.JsonSerializer.Deserialize<AiTavsiyeSonucu>(aiCevabi, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                                return Json(sonuc);
                            }
                        }
                    }
                    return StatusCode(500, "Gemini API'den geçerli bir yanıt alınamadı. Lütfen daha sonra tekrar deneyin.");
                }
            }
            catch (Exception)
            {
                // 🛡️ GÜVENLİK: Information Disclosure (Tablo/Ağaç sızıntısı) önlemi
                return View("~/Views/Error/Error.cshtml", new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
            }
        }

        public async Task TripadvisordanAlamadigimizMisafirUlkesiniGeminiTahminEdipGuncellesinTopluAsync()
        {
            var user = GetActiveUser();
            string geminiApiKey = _configuration["GeminiApi:ApiKey"];
            string apiUrl = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key={geminiApiKey}";

            // BÜTÜN YORUMLARI ÇEKİYORUZ
            //List<Yorum> tumYorumlar = await _context.Yorum
            //    .Where(y => y.HotelRef == user.DepartmanRefNavigation.HotelRef)
            //    .OrderByDescending(y => y.MisafirYorumTarihi)
            //    .ToListAsync();
            List<Yorum> tumYorumlar = await _context.Yorum
            .Where(y => y.HotelRef == user.DepartmanRefNavigation.HotelRef && !string.IsNullOrEmpty(y.MisafirUlkesi))
            .OrderByDescending(y => y.MisafirYorumTarihi)
            .ToListAsync();



            // 1. TASARRUF FİLTRESİ: Yorumu boş olanları veya zaten Türkçe standart ülke olanları listeye hiç alma
            var islenecekYorumlar = tumYorumlar.Where(y =>
                !string.IsNullOrWhiteSpace(y.MisafirYorum) &&
                !(y.MisafirUlkesi != null && (y.MisafirUlkesi.Trim().ToLower() == "türkiye" || y.MisafirUlkesi.Trim().ToLower() == "almanya" || y.MisafirUlkesi.Trim().ToLower() == "rusya" || y.MisafirUlkesi.Trim().ToLower() == "ingiltere" || y.MisafirUlkesi.Trim().ToLower() == "kazakistan" || y.MisafirUlkesi.Trim().ToLower() == "ukrayna"))
            ).ToList();

            // 2. CHUNKING (GRAPLAMA): Listeyi 20'şerli paketlere bölüyoruz!
            var gruplar = islenecekYorumlar.Chunk(20).ToList();

            using (var httpClient = new System.Net.Http.HttpClient())
            {
                foreach (var grup in gruplar)
                {
                    // Bu 20'lik grup için prompt içine gömeceğimiz JSON verisini hazırlayalım
                    System.Text.StringBuilder sbYorumlar = new System.Text.StringBuilder();
                    for (int i = 0; i < grup.Length; i++)
                    {
                        // Yorumun içindeki tırnaklar JSON yapısını bozmasın diye tek tırnağa çeviriyoruz
                        string temizYorum = grup[i].MisafirYorum.Replace("\"", "'").Replace("\n", " ");
                        string temizUlke = string.IsNullOrWhiteSpace(grup[i].MisafirUlkesi) ? "" : grup[i].MisafirUlkesi.Trim();

                        sbYorumlar.AppendLine($@"{{ ""id"": {i}, ""mevcutUlke"": ""{temizUlke}"", ""yorum"": ""{temizYorum}"" }},");
                    }

                    // Sondaki fazladan virgülü temizleyelim
                    string jsonVeri = sbYorumlar.ToString().TrimEnd(',', '\r', '\n');

                    // 3. GELİŞMİŞ JSON PROMPT'U (Hem Dedektif Hem Çevirmen)
                    string prompt = $@"Sen uzman bir dilbilimci ve çevirmensin. Sana JSON formatında 20 adet otel yorumu veriyorum.
                                            Her bir kayıt için şu işlemi yap:
                                            - Eğer 'mevcutUlke' BOŞ ise: 'yorum' metnindeki makine çevirisi hatalarını analiz edip misafirin ülkesini tahmin et ve TÜRKÇE yaz.
                                            - Eğer 'mevcutUlke' DOLU ise: O kelimeyi sadece Türkçeye çevir (Örn: Germany -> Almanya).

                                            Kesin Kurallar:
                                            1. SADECE bir JSON dizisi (array) döndür.
                                            2. Format kesinlikle şöyle olmalı: [{{""id"": 0, ""ulke"": ""Türkiye""}}, {{""id"": 1, ""ulke"": ""Rusya""}}]
                                            3. Başında ve sonunda ```json gibi markdown işaretleri OLMASIN. Saf JSON ver.
                                            4. Bulamazsan 'Bilinmiyor' yaz.

                                            İşlenecek Veriler:
                                            [
                                            {jsonVeri}
                                            ]";

                    try
                    {
                        var requestData = new
                        {
                            contents = new[] { new { parts = new[] { new { text = prompt } } } },
                            generationConfig = new { temperature = 0.1 }
                        };

                        var content = new System.Net.Http.StringContent(System.Text.Json.JsonSerializer.Serialize(requestData), System.Text.Encoding.UTF8, "application/json");
                        var response = await httpClient.PostAsync(apiUrl, content);

                        if (response.IsSuccessStatusCode)
                        {
                            string jsonResponse = await response.Content.ReadAsStringAsync();
                            using (System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(jsonResponse))
                            {
                                var root = doc.RootElement;
                                var candidates = root.GetProperty("candidates");
                                if (candidates.GetArrayLength() > 0)
                                {
                                    string aiCevabi = candidates[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();

                                    // Gemini kural dinlemeyip başına sonuna markdown eklerse temizliyoruz
                                    aiCevabi = aiCevabi?.Replace("```json", "").Replace("```", "").Trim();

                                    // 4. GEMINI'NİN JSON CEVABINI C# İLE OKUYUP EŞLEŞTİRME
                                    using (System.Text.Json.JsonDocument cevapDoc = System.Text.Json.JsonDocument.Parse(aiCevabi))
                                    {
                                        foreach (var eleman in cevapDoc.RootElement.EnumerateArray())
                                        {
                                            int id = eleman.GetProperty("id").GetInt32();
                                            string bulunanUlke = eleman.GetProperty("ulke").GetString();

                                            if (!string.IsNullOrWhiteSpace(bulunanUlke) && bulunanUlke != "Bilinmiyor")
                                            {
                                                // ID sayesinde 20'lik gruptaki doğru yorumu bulup ülkesini yazıyoruz
                                                grup[id].MisafirUlkesi = bulunanUlke;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // Bir grupta (20'lik pakette) API hatası olursa çökmek yerine diğer gruba geçecek
                    }

                    // 5. MÜKEMMEL DENGE: Her 20'lik paket başarıyla işlendiğinde SQL'e kaydet! 
                    // Ne sistemi yorar, ne de hata durumunda veri kaybı yaşatır.
                    await _context.SaveChangesAsync();
                }
            }
        }
    }
}