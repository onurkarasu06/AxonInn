using AxonInn.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json; // DEĞİŞİKLİK: Ağır Newtonsoft yerine Microsoft'un yüksek performanslı yerleşik kütüphanesi eklendi

namespace AxonInn.Controllers
{
    public class LoginController : Controller
    {
        private readonly AxonInnContext _context;

        public LoginController(AxonInnContext context)
        {
            _context = context;
        }

        [HttpGet]
        public ActionResult Login()
        {
            try
            {
                // PERFORMANS: Eğer kullanıcı zaten giriş yapmışsa direkt yönlendir, sayfayı boşuna render etme.
                var mevcutSession = HttpContext.Session.GetString("GirisYapanPersonel");
                if (!string.IsNullOrEmpty(mevcutSession))
                {
                    return RedirectToAction("Ana", "Ana");
                }

                return View();
            }
            catch (Exception ex)
            {
                return View("~/Views/Error/Error.cshtml", new ErrorViewModel { RequestId = ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken] // GÜVENLİK: Dışarıdan sahte form gönderimlerini (CSRF / Bot saldırılarını) engeller.
        public async Task<ActionResult> Login(string identifier, string password)
        {
            try
            {
                if (!string.IsNullOrEmpty(identifier) && !string.IsNullOrEmpty(password))
                {
                    // PERFORMANS: Include etmeye veya tüm objeyi çekmeye gerek yok.
                    // Veritabanından (SQL seviyesinde) sadece ihtiyacımız olan sütunları Select ile çekiyoruz.
                    // Bu sayede sessionPersonel gibi ikinci bir kopyalama işlemine gerek kalmıyor.
                    var personel = await _context.Personels
                        .AsNoTracking()
                        .Where(p => (p.MailAdresi == identifier || p.TelefonNumarasi == identifier)
                                 && p.Sifre == password
                                 && p.AktifMi == 1)
                        .Select(p => new Personel
                        {
                            Id = p.Id,
                            Adi = p.Adi,
                            Soyadi = p.Soyadi,
                            Yetki = p.Yetki,
                            DepartmanRef = p.DepartmanRef,
                            MailAdresi = p.MailAdresi,
                            TelefonNumarasi = p.TelefonNumarasi
                        })
                        .FirstOrDefaultAsync();

                    if (personel != null)
                    {
                        bool logKayit = await LogKaydet(personel, "Sisteme Giriş Başarılı", "Login İşlemi", identifier);

                        if (logKayit)
                        {
                            // PERFORMANS: Newtonsoft yerine yüksek hızlı System.Text.Json kullanımı
                            var personelJson = JsonSerializer.Serialize(personel);
                            HttpContext.Session.SetString("GirisYapanPersonel", personelJson);

                            return RedirectToAction("Ana", "Ana");
                        }
                        else
                        {
                            TempData["ErrorMessage"] = "Giriş Loglanamadı.";
                            return RedirectToAction("Login", "Login");
                        }
                    }
                    else
                    {
                        await LogKaydet(null, "Hatalı Giriş Denemesi", $"Kullanıcı Bulunamadı. Denenen: {identifier}", identifier);
                        TempData["ErrorMessage"] = "Email-Telefon Numarası veya şifre hatalı!";
                        return RedirectToAction("Login", "Login");
                    }
                }
                else
                {
                    TempData["ErrorMessage"] = "Lütfen Email/Telefon ve Şifre alanlarını doldurunuz!";
                    return RedirectToAction("Login", "Login");
                }
            }
            catch (Exception ex)
            {
                return View("~/Views/Error/Error.cshtml", new ErrorViewModel { RequestId = ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> Logout()
        {
            try
            {
                var personelJson = HttpContext.Session.GetString("GirisYapanPersonel");
                if (string.IsNullOrEmpty(personelJson))
                    return RedirectToAction("Login", "Login");

                // PERFORMANS: System.Text.Json ile Deserialization
                var loginOlanPersonel = JsonSerializer.Deserialize<Personel>(personelJson);

                // GÜVENLİK: Olası null hatasına karşı güvenlik önlemi (?? "") eklendi
                bool logkayit = await LogKaydet(loginOlanPersonel, "Güvenli Çıkış Yapıldı", "Logout İşlemi", loginOlanPersonel?.MailAdresi ?? "");

                if (logkayit)
                {
                    HttpContext.Session.Clear();
                    return RedirectToAction("Login", "Login");
                }
                else
                {
                    return View("~/Views/Error/Error.cshtml", new ErrorViewModel { RequestId = "Çıkış İşlemi Loglanamadı" });
                }
            }
            catch (Exception ex)
            {
                return View("~/Views/Error/Error.cshtml", new ErrorViewModel { RequestId = ex.Message });
            }
        }

        private async Task<bool> LogKaydet(Personel? personel, string islemTipi, string yeniDeger, string girilenVeri)
        {
            try
            {
                string hotelAdi = "";
                string departmanAdi = "";

                if (personel != null && personel.DepartmanRef != 0)
                {
                    // Sizin yazdığınız harika performanslı tek SQL sorgusu korundu.
                    var depBilgisi = await _context.Departmen
                        .AsNoTracking()
                        .Where(d => d.Id == personel.DepartmanRef)
                        .Select(d => new { d.Adi, HotelAdi = d.HotelRefNavigation.Adi })
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
                    IlgiliTablo = "Personel",
                    KayitRefId = personel?.Id ?? 0,
                    IslemTipi = islemTipi,
                    EskiDeger = girilenVeri ?? "",
                    YeniDeger = yeniDeger ?? "",
                    YapanHotelAd = hotelAdi,
                    YapanDepartmanAd = departmanAdi,
                    YapanAdSoyad = personel != null ? $"{personel.Adi} {personel.Soyadi}" : "Bilinmeyen Kullanıcı"
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