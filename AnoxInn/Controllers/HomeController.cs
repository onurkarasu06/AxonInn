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
        [Route("")] // Uygulama açıldığında direkt buraya gelmesi için
        public async Task<IActionResult> Index()
        {
            try
            {
                var personelJson = HttpContext.Session.GetString("GirisYapanPersonel");
                if (string.IsNullOrEmpty(personelJson))
                    return RedirectToAction("Login", "Login");

                var loginOlanPersonel = JsonConvert.DeserializeObject<Personel>(personelJson);

                // Ana sayfaya giriş logu
                await LogKaydet(loginOlanPersonel, "Ana Sayfaya Giriş Yapıldı", "Dashboard Görüntüleme");

                // Kullanıcının bağlı olduğu departmanın Hotel ID'sini bul
                var hotelId = await _context.Departmen
                    .Where(d => d.Id == loginOlanPersonel.DepartmanRef)
                    .Select(d => d.HotelRef)
                    .FirstOrDefaultAsync();

                // Tek sorguda Hotel -> Departmanlar -> Aktif Personeller -> Personel Görevleri zincirini çekiyoruz
                var hotel = await _context.Hotels
                    .Include(h => h.Departmen)
                        .ThenInclude(d => d.Personels.Where(p => p.AktifMi == 1))
                            .ThenInclude(p => p.Gorevs)
                    .FirstOrDefaultAsync(h => h.Id == hotelId);

                if (hotel == null)
                    return RedirectToAction("Login", "Login");

                // View'a doğrudan EF Core Hotel modelini yolluyoruz (Tıpkı View sayfasında tanımladığımız gibi)
                return View(hotel);
            }
            catch (Exception ex)
            {
                return View("~/Views/Error/Error.cshtml", new ErrorViewModel { RequestId = ex.Message });
            }
        }

      

        // Local Loglama Metodu
        private async Task<bool> LogKaydet(Personel? personel, string islemTipi, string yeniDeger)
        {
            try
            {
                string departmanAdi = personel?.DepartmanRefNavigation?.Adi ?? "";
                string hotelAdi = "";

                if (personel != null && personel.DepartmanRef != 0)
                {
                    var hotelId = await _context.Departmen
                        .Where(d => d.Id == personel.DepartmanRef)
                        .Select(d => d.HotelRef)
                        .FirstOrDefaultAsync();

                    if (hotelId != 0)
                    {
                        hotelAdi = await _context.Hotels.Where(h => h.Id == hotelId).Select(h => h.Adi).FirstOrDefaultAsync() ?? "";
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
                return false; // Sayfa çökmesin diye hatayı yutuyoruz
            }
        }
    }
}