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
            List<Yorum> yorumList = await yorumIslem.TripadvisorYorumGetirAsync(hotelID, getirilecekKayitAdeti, apiToken);
            await yorumIslem.TripadvisorYorumKaydetAsync(hotelID, yorumList, _context);
            return null;
        }
        public IActionResult GeminiAnalizleriKaydet()
        {
            long hotelID = 1;
            string geminiApiKey = _configuration["GeminiApi:ApiKey"];
            YorumIslem yorumIslem = new YorumIslem();
            List<Yorum> dbYorumList = yorumIslem.GeminiAnaliziOlmayanVeritabaniYorumListGetirAsync(hotelID, _context)
                                                .GetAwaiter()
                                                .GetResult();
            if (dbYorumList == null || !dbYorumList.Any())
                return Ok("Analiz edilecek yeni yorum bulunamadı.");
            string topluAnalizCevabi = yorumIslem.GeminiYorumAnaliziYap(dbYorumList, geminiApiKey);
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
                            var dbYorum = dbYorumList.FirstOrDefault(y => y.MisafirYorumId == currentYorumId.ToString().Trim());
                            if (dbYorum != null)
                                dbYorum.GeminiVerileriniIsle(analizItem.GetRawText());
                        }
                    }
                }
                _context.SaveChanges();
            }
            return Ok("Analizler başarıyla tamamlandı ve kaydedildi.");
        }
    }
}