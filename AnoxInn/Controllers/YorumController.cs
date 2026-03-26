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

        private Departman GetSessionBilgisi(Personel loginOlanPersonel)
        {
            var sessionBilgisi = _context.Departmen.AsNoTracking()
                                            .Include(d => d.HotelRefNavigation)
                                            .FirstOrDefault(d => d.Id == loginOlanPersonel.DepartmanRef);

            return sessionBilgisi;
        }

        public async Task<IActionResult> Yorum()
        {
            try
            {
                var loginOlanPersonel = GetActiveUser();
                if (loginOlanPersonel == null)
                    return RedirectToAction("Login", "Login");

                var sessionBilgisi = GetSessionBilgisi(loginOlanPersonel);
                ViewBag.HotelAdi = sessionBilgisi.HotelRefNavigation.Adi;
                long aktifOtelId = sessionBilgisi.HotelRefNavigation.Id;

                List<Yorum> yorumList = await _context.Yorum.Where(y => y.HotelRef == aktifOtelId).ToListAsync();

                YorumDashboardGrafikServisi yorumDashboardGrafikServisi = new YorumDashboardGrafikServisi();
                YorumDashboardViewModel yorumDashboardViewModel = new YorumDashboardViewModel();

                yorumDashboardViewModel.KpiVerileri = yorumDashboardGrafikServisi.HesaplaKpiKartlari(yorumList);
                yorumDashboardViewModel.DuyguGrafik = yorumDashboardGrafikServisi.HesaplaDuyguPastaGrafigi(yorumList);
                yorumDashboardViewModel.DepartmanGrafik = yorumDashboardGrafikServisi.HesaplaDepartmanBarGrafigi(yorumList);
                yorumDashboardViewModel.HisPolarGrafik = yorumDashboardGrafikServisi.HesaplaHisPolarGrafigi(yorumList);
                yorumDashboardViewModel.KelimeGrafik = yorumDashboardGrafikServisi.HesaplaKelimeBarGrafigi(yorumList);
                yorumDashboardViewModel.UlkeGrafik = yorumDashboardGrafikServisi.HesaplaUlkeGrafigi(yorumList);
                yorumDashboardViewModel.KonaklamaGrafik = yorumDashboardGrafikServisi.HesaplaKonaklamaTipiGrafigi(yorumList);
                yorumDashboardViewModel.TrendGrafik = yorumDashboardGrafikServisi.HesaplaAylikTrendGrafigi(yorumList);

                return View(yorumDashboardViewModel);
            }
            catch (Exception)
            {
                return View("~/Views/Error/Error.cshtml", new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
            }
        }

        public async Task<IActionResult> TripadvisorYorumlariKaydet()
        {
            long hotelID = 1;
            int getirilecekKayitAdeti = 20;
            string apiToken = _configuration["ApifyApiToken:ApiToken"];
            YorumIslem yorumIslem = new YorumIslem();
            List<Yorum> yorumList = yorumIslem.TripadvisorYorumGetirApifyApi(hotelID, getirilecekKayitAdeti, apiToken);
            yorumIslem.TripadvisorYorumKaydet(hotelID, yorumList, _context);
            return RedirectToAction("Yorum"); // İşlem bitince sayfaya dön
        }

        public async Task<IActionResult> TripadvisorYorumlariRapidApiKaydet()
        {
            long hotelID = 1;
            long tripadvisorHotelID = 23426767;
            int getirilecekKayitAdeti = 1000;
            string apiToken = _configuration["RapidApiToken:ApiToken"];
            YorumIslem yorumIslem = new YorumIslem();
            List<Yorum> yorumList = yorumIslem.TripadvisorYorumGetirRapidApi(hotelID, tripadvisorHotelID, getirilecekKayitAdeti, apiToken);
            yorumIslem.TripadvisorYorumKaydet(hotelID, yorumList, _context);
            return RedirectToAction("Yorum"); // İşlem bitince sayfaya dön
        }

        public async Task<IActionResult> GeminiAnalizleriKaydetAsync()
        {
            long hotelID = 1;
            string geminiApiKey = _configuration["GeminiApi:ApiKey"];
            YorumIslem yorumIslem = new YorumIslem();
            List<Yorum> dbYorumList = await yorumIslem.GeminiAnaliziOlmayanVeritabaniYorumListGetirAsync(hotelID, _context);

            if (dbYorumList == null || !dbYorumList.Any())
                return Ok("Analiz edilecek yeni yorum bulunamadı.");

            int basariylaIslenenYorumSayisi = await yorumIslem.YorumlariPartilerHalindeIsleAsync(dbYorumList, yorumIslem, geminiApiKey, _context);

            return RedirectToAction("Yorum");
        }


        [HttpPost]
        public async Task<IActionResult> YapayZekaTavsiyesiAl([FromBody] YorumDashboardViewModel panelVerisi)
        {
            try
            {
                // 1. Veriyi JSON'a çeviriyoruz ki Gemini grafikleri okuyabilsin
                string jsonVeri = System.Text.Json.JsonSerializer.Serialize(panelVerisi);

                // 2. Sihirli Prompt: Gemini'yi nokta atışı kısa tavsiyelere zorluyoruz
                string prompt = $@"Sen AxonInn otel yönetim sistemi için çalışan kıdemli bir Turizm Stratejisti ve Veri Bilimcisisin. 
                                    Aşağıda otelimizin güncel 7 farklı analiz grafiğinin verilerini JSON olarak veriyorum:
                                    {jsonVeri}

                                    Lütfen bu verileri detaylıca incele ve otel müdürü için aksiyon alınabilir, net ve profesyonel tavsiyeler üret.
                                    ÇOK ÖNEMLİ KURALLAR:
                                    1- Arayüzde (UI) yerimiz çok kısıtlı! Her bir tavsiye KESİNLİKLE EN FAZLA 5 KISA CÜMLE olmalı. Lafı uzatma, doğrudan sorunu 
                                    söyle ve çözüm öner.
                                    2- SADECE aşağıdaki JSON formatında cevap ver. Markdown karakterleri (```json vb.) veya ekstra açıklamalar KULLANMA.

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

                // 3. AppSettings.json'dan Gemini API Anahtarını alıyoruz
                string geminiApiKey = _configuration["GeminiApi:ApiKey"];

                // DÜZELTME: Senin kullandığın güncel 2.5 Flash modeline çekildi
                string apiUrl = $"[https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key=](https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key=){geminiApiKey}";

                using (var httpClient = new System.Net.Http.HttpClient())
                {
                    var requestData = new
                    {
                        contents = new[] { new { parts = new[] { new { text = prompt } } } },
                        generationConfig = new
                        {
                            temperature = 0.2, // Yaratıcılığı kısıp net analitik cevaplar almasını sağlıyoruz
                            response_mime_type = "application/json" // Gemini'yi JSON harici bir şey yazmamaya zorluyoruz
                        }
                    };

                    var content = new System.Net.Http.StringContent(System.Text.Json.JsonSerializer.Serialize(requestData), System.Text.Encoding.UTF8, "application/json");
                    var response = await httpClient.PostAsync(apiUrl, content);

                    if (response.IsSuccessStatusCode)
                    {
                        string jsonResponse = await response.Content.ReadAsStringAsync();

                        // Gemini'nin karmaşık API yanıtından sadece bizim JSON metnini ayıklıyoruz
                        using (System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(jsonResponse))
                        {
                            var root = doc.RootElement;
                            var candidates = root.GetProperty("candidates");
                            if (candidates.GetArrayLength() > 0)
                            {
                                string aiCevabi = candidates[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();

                                // Olası markdown işaretlerini güvenlik amaçlı temizliyoruz
                                aiCevabi = aiCevabi.Replace("```json", "").Replace("```", "").Trim();

                                var sonuc = System.Text.Json.JsonSerializer.Deserialize<AiTavsiyeSonucu>(aiCevabi);
                                return Json(sonuc);
                            }
                        }
                    }

                    return StatusCode(500, "Gemini API'den geçerli bir yanıt alınamadı. Lütfen daha sonra tekrar deneyin.");
                }
            }
            catch (Exception ex)
            {
                // Sistem çökmesini önlemek için hata durumunda konsola yakalayıp JSON dönüyoruz
                return StatusCode(500, new { error = ex.Message });
            }
        }
    }
}