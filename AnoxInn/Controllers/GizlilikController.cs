using AxonInn.Models.Context;
using AxonInn.Models.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json; // ⚡ Yüksek Hızlı Yeni Nesil JSON kütüphanesine geçildi

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
        public async Task<IActionResult> Gizlilik()
        {
            try
            {
                var personelJson = HttpContext.Session.GetString("GirisYapanPersonel");
                if (string.IsNullOrEmpty(personelJson))
                    return RedirectToAction("Login", "Login");

                // ⚡ RAM OPTİMİZASYONU: Newtonsoft yerine bellek dostu JsonSerializer kullanıldı
                var loginOlanPersonel = JsonSerializer.Deserialize<Personel>(personelJson);

                // ⚡ İŞLEMCİ (CPU) OPTİMİZASYONU: Layout vb. sayfalarda tekrar JSON çözümlenmemesi için nesne ViewData'ya aktarıldı
                ViewData["GirisYapanPersonel"] = loginOlanPersonel;

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
                string departmanAdi = "";
                string hotelAdi = "";

                if (personel != null && personel.DepartmanRef != 0)
                {
                    // ⚡ DB OPTİMİZASYONU & HATA GİDERİMİ: 
                    // Session içindeki JSON ilişkili nesneleri (Navigation Properties) taşımaz. 
                    // Bu yüzden Departman adını da Otel adıyla birlikte TEK bir AsNoTracking sorgusu ile anında alıyoruz.
                    var depBilgisi = await _context.Departmen
                        .AsNoTracking()
                        .Where(d => d.Id == personel.DepartmanRef)
                        .Select(d => new { d.Adi, HotelAdi = d.HotelRefNavigation != null ? d.HotelRefNavigation.Adi : "" })
                        .FirstOrDefaultAsync();

                    if (depBilgisi != null)
                    {
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