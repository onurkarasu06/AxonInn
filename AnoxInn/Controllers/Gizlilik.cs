using AxonInn.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace AxonInn.Controllers
{
    public class GizlilikController : Controller
    {
        private readonly AxonInnContext _context;

        public GizlilikController(AxonInnContext context)
        {
            _context = context;
        }

        [Route("Gizlilik")]
        public async Task<IActionResult> Index()
        {
            try
            {
                var personelJson = HttpContext.Session.GetString("GirisYapanPersonel");
                if (string.IsNullOrEmpty(personelJson))
                    return RedirectToAction("Login", "Login");

                var loginOlanPersonel = JsonConvert.DeserializeObject<Personel>(personelJson);

                await LogKaydet(loginOlanPersonel, "Gizlilik Sayfasına Giriş Yapıldı", "Gizlilik Görüntüleme");

                // ÇÖZÜM BURADA: Action adı Index olsa bile, ekrana Gizlilik.cshtml dosyasını basar
                return View("Gizlilik");
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
                    // DEĞİŞİKLİK: 2 ayrı SQL sorgusu yerine Navigation Property ile tek sorguya (JOIN) düşürüldü.
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