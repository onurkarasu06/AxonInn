using AxonInn.Apify;
using AxonInn.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.Text.Json;

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
            if (string.IsNullOrEmpty(personelJson)) return null;
            return System.Text.Json.JsonSerializer.Deserialize<Personel>(personelJson);
        }
        private async Task<Departman> GetSessionBilgisiAsync(Personel loginOlanPersonel)
        {
            var sessionBilgisi = await _context.Departmen.AsNoTracking()
                                            .Include(d => d.HotelRefNavigation)
                                            .FirstOrDefaultAsync(d => d.Id == loginOlanPersonel.DepartmanRef);

            return sessionBilgisi;
        }
        public async Task<IActionResult> Yorum()
        {
            try
            {
                var loginOlanPersonel = GetActiveUser();
                if (loginOlanPersonel == null) return RedirectToAction("Login", "Login");
                var sessionBilgisi = await _context.Departmen.AsNoTracking()
                                                             .Where(d => d.Id == loginOlanPersonel.DepartmanRef)
                                                             .Select(d => new
                                                             {
                                                                 d.HotelRef,
                                                                 HotelAdi = d.HotelRefNavigation != null ? d.HotelRefNavigation.Adi : "Bilinmeyen Otel",
                                                                 DepartmanAdi = d.Adi
                                                             })
                                                                .FirstOrDefaultAsync();
                ViewBag.HotelAdi = sessionBilgisi.HotelAdi;
                YorumIslem yorumIslem = new YorumIslem();
                List<Yorum> yorumList = await yorumIslem.GeminiAnaliziOlanVeritabaniYorumListGetirAsync(loginOlanPersonel.DepartmanRefNavigation.HotelRef, _context);
                return View(yorumList);
            }
            catch (Exception)
            {
                return View("~/Views/Error/Error.cshtml", new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
            }
        }
        public async Task<IActionResult> TripadvisorYorumlariKaydet()
        {
            // veri hazırlamak için ben kullanacagım sadece
            long hotelID = 1;
            int getirilecekKayitAdeti = 20;
            string apiToken = _configuration["ApifyApiToken:ApiToken"];
            YorumIslem yorumIslem = new YorumIslem();
            List<Yorum> yorumList = yorumIslem.TripadvisorYorumGetirApifyApi(hotelID, getirilecekKayitAdeti, apiToken);
            yorumIslem.TripadvisorYorumKaydet(hotelID, yorumList, _context);
            return null;
        }

        public async Task<IActionResult> TripadvisorYorumlariRapidApiKaydet()
        {
            // veri hazırlamak için ben kullanacagım sadece
            long hotelID = 1;
            long tripadvisorHotelID = 23426767;
            int getirilecekKayitAdeti = 1000;
            string apiToken = _configuration["RapidApiToken:ApiToken"];
            YorumIslem yorumIslem = new YorumIslem();
            List<Yorum> yorumList = yorumIslem.TripadvisorYorumGetirRapidApi(hotelID,tripadvisorHotelID, getirilecekKayitAdeti, apiToken);
            yorumIslem.TripadvisorYorumKaydet(hotelID, yorumList, _context);
            return null;
        }

        public IActionResult GeminiAnalizleriKaydet()
        {
            long hotelID = 1;
            string geminiApiKey = _configuration["GeminiApi:ApiKey"];
            YorumIslem yorumIslem = new YorumIslem();

            // Veritabanından analiz edilecek yorumları senkron olarak çekiyoruz
            List<Yorum> dbYorumList = yorumIslem.GeminiAnaliziOlmayanVeritabaniYorumListGetirAsync(hotelID, _context)
                                                .GetAwaiter()
                                                .GetResult();

            if (dbYorumList == null || !dbYorumList.Any())
                return Ok("Analiz edilecek yeni yorum bulunamadı.");

            // PARÇALAMA (BATCHING) AYARLARI
            int partiBuyuklugu = 15; // Gemini'ye her defasında 10 yorum göndereceğiz (Timeout yememek için ideal sayı)
            int toplamPartiSayisi = (int)Math.Ceiling((double)dbYorumList.Count / partiBuyuklugu);
            int basariylaIslenenYorumSayisi = 0;

            for (int i = 0; i < toplamPartiSayisi; i++)
            {
                // O anki 10'luk grubu (partiyi) listeden çekiyoruz
                var suAnkiParti = dbYorumList.Skip(i * partiBuyuklugu).Take(partiBuyuklugu).ToList();

                try
                {
                    // Gemini'ye sadece bu 10 yorumu gönderiyoruz
                    string topluAnalizCevabi = yorumIslem.GeminiYorumAnaliziYap(suAnkiParti, geminiApiKey);

                    using JsonDocument doc = JsonDocument.Parse(topluAnalizCevabi);
                    JsonElement root = doc.RootElement;

                    if (root.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement analizItem in root.EnumerateArray())
                        {
                            if (analizItem.TryGetProperty("YorumId", out JsonElement idElement))
                            {
                                string idString = idElement.ToString();
                                if (long.TryParse(idString, out long currentYorumId))
                                {
                                    // Aramayı tüm listede değil, sadece o anki 10'luk parti içinde yapıyoruz (Hız kazandırır)
                                    var dbYorum = suAnkiParti.FirstOrDefault(y => y.MisafirYorumId == currentYorumId.ToString().Trim());

                                    if (dbYorum != null)
                                    {
                                        dbYorum.GeminiVerileriniIsle(analizItem.GetRawText());
                                        basariylaIslenenYorumSayisi++;
                                    }
                                }
                            }
                        }

                        // HER PARTİDEN SONRA VERİTABANINA KAYDET (SENKRON)
                        // Bu sayede sistem 3. partide patlasa bile, ilk 2 parti veritabanına çoktan işlenmiş olur!
                        _context.SaveChanges();
                    }

                    // GEMİNİ'Yİ BOĞMAMAK İÇİN KISA BİR MOLA (RATE LIMIT KORUMASI)
                    // Son parti değilse, bir sonraki isteği atmadan önce 3 saniye (3000 ms) bekliyoruz
                    if (i < toplamPartiSayisi - 1)
                    {
                        Thread.Sleep(5000);
                    }
                }
                catch (Exception ex)
                {
                    // EĞER BİR PARTİDE HATA ÇIKARSA: 
                    // Döngü kırılmaz, hata yoksayılır ve sistem bir sonraki 10'luk partiyi analiz etmeye devam eder.
                    // İstersen buraya loglama (Console.WriteLine vb.) ekleyebilirsin.
                }
            }

            return Ok($"Toplam {dbYorumList.Count} yorumdan {basariylaIslenenYorumSayisi} tanesi 10'arlı parçalar halinde başarıyla analiz edildi ve kaydedildi.");
        }
    }
}