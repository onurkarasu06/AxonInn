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
                ViewBag.HotelAdi = GetSessionBilgisi(loginOlanPersonel).HotelRefNavigation.Adi;
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
            long hotelID = 1;
            long tripadvisorHotelID = 23426767;
            int getirilecekKayitAdeti = 1000;
            string apiToken = _configuration["RapidApiToken:ApiToken"];
            YorumIslem yorumIslem = new YorumIslem();
            List<Yorum> yorumList = yorumIslem.TripadvisorYorumGetirRapidApi(hotelID,tripadvisorHotelID, getirilecekKayitAdeti, apiToken);
            yorumIslem.TripadvisorYorumKaydet(hotelID, yorumList, _context);
            return null;
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
            return Ok($"Toplam {dbYorumList.Count} yorumdan {basariylaIslenenYorumSayisi} tanesi parçalar halinde başarıyla analiz edildi ve kaydedildi.");
        }
    }
}