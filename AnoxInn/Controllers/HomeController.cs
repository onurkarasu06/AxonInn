using AxonInn.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace AxonInn.Controllers
{
    public class HomeController : Controller
    {
        private readonly AxonInnContext _context;

        public HomeController(AxonInnContext context)
        {
            _context = context;
        }

        [Route("AnaSayfa/Index")]
        [Route("")]
        public async Task<IActionResult> Index()
        {
            try
            {
                var personelJson = HttpContext.Session.GetString("GirisYapanPersonel");
                if (string.IsNullOrEmpty(personelJson))
                    return RedirectToAction("Login", "Login");

                var loginOlanPersonel = JsonConvert.DeserializeObject<Personel>(personelJson);
                await LogKaydet(loginOlanPersonel, "Ana Sayfaya Giriş Yapıldı", "Dashboard Görüntüleme");

                // Otel ID'sini bul
                var hotelId = await _context.Departmen
                    .Where(d => d.Id == loginOlanPersonel.DepartmanRef)
                    .Select(d => d.HotelRef)
                    .FirstOrDefaultAsync();

                if (hotelId == 0) return RedirectToAction("Login", "Login");

                // DEĞİŞİKLİK 1: RAM'e almak yerine doğrudan SQL'e saydırıyoruz (Çok Hızlıdır)
                var hotelAdi = await _context.Hotels.Where(h => h.Id == hotelId).Select(h => h.Adi).FirstOrDefaultAsync();

                var aktifPersonelAdet = await _context.Personels
                    .CountAsync(p => p.DepartmanRefNavigation.HotelRef == hotelId && p.AktifMi == 1);

                var beklemedeAdet = await _context.Gorevs
                    .CountAsync(g => g.PersonelRefNavigation.DepartmanRefNavigation.HotelRef == hotelId && g.Durum == 1);

                var islemdeAdet = await _context.Gorevs
                    .CountAsync(g => g.PersonelRefNavigation.DepartmanRefNavigation.HotelRef == hotelId && g.Durum == 2);

                var bittiAdet = await _context.Gorevs
                    .CountAsync(g => g.PersonelRefNavigation.DepartmanRefNavigation.HotelRef == hotelId && g.Durum == 3);

                // DEĞİŞİKLİK 2: Grafikler için tüm tabloyu değil, sadece ad, soyad ve durum çekiyoruz (Select)
                var personelChartData = await _context.Personels
                    .Where(p => p.DepartmanRefNavigation.HotelRef == hotelId && p.AktifMi == 1)
                    .Select(p => new {
                        ad = p.Adi,
                        soyad = p.Soyadi,
                        departman = new { ad = p.DepartmanRefNavigation.Adi }
                    }).ToListAsync();

                var gorevChartData = await _context.Gorevs
                    .Where(g => g.PersonelRefNavigation.DepartmanRefNavigation.HotelRef == hotelId)
                    .Select(g => new {
                        durum = g.Durum,
                        personel = new
                        {
                            id = g.PersonelRef,
                            ad = g.PersonelRefNavigation.Adi,
                            departman = new { ad = g.PersonelRefNavigation.DepartmanRefNavigation.Adi }
                        }
                    }).ToListAsync();

                // View'daki filtre için sadece departman listesi
                var departmanlar = await _context.Departmen
                    .Where(d => d.HotelRef == hotelId)
                    .Select(d => new Departman { Id = d.Id, Adi = d.Adi })
                    .ToListAsync();

                // Verileri View'a ViewBag ile gönderiyoruz (Ağır Model yapısını bıraktık)
                ViewBag.HotelAdi = hotelAdi;
                ViewBag.AktifPersonelAdet = aktifPersonelAdet;
                ViewBag.BeklemedeAdet = beklemedeAdet;
                ViewBag.IslemdeAdet = islemdeAdet;
                ViewBag.BittiAdet = bittiAdet;
                ViewBag.PersonelJson = JsonConvert.SerializeObject(personelChartData);
                ViewBag.GorevJson = JsonConvert.SerializeObject(gorevChartData);
                ViewBag.Departmanlar = departmanlar;

                return View();
            }
            catch (Exception ex)
            {
                return View("~/Views/Error/Error.cshtml", new ErrorViewModel { RequestId = ex.Message });
            }
        }

        private async Task<bool> LogKaydet(Personel? personel, string islemTipi, string yeniDeger)
        {
            try
            {
                string departmanAdi = personel?.DepartmanRefNavigation?.Adi ?? "";
                string hotelAdi = "";

                if (personel != null && personel.DepartmanRef != 0)
                {
                    // DEĞİŞİKLİK 3: Join mantığıyla tek SQL sorgusu
                    hotelAdi = await _context.Departmen
                        .Where(d => d.Id == personel.DepartmanRef)
                        .Select(d => d.HotelRefNavigation.Adi)
                        .FirstOrDefaultAsync() ?? "";
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