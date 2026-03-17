using AxonInn.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

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
        [ValidateAntiForgeryToken]
        public async Task<ActionResult> Login(string identifier, string password)
        {
            try
            {
                if (!string.IsNullOrEmpty(identifier) && !string.IsNullOrEmpty(password))
                {
                    identifier = identifier.Trim();

                    // --- YENİ: FORMAT KONTROLÜ ---
                    if (identifier.Contains("@"))
                    {
                        var emailValidator = new System.ComponentModel.DataAnnotations.EmailAddressAttribute();
                        if (!emailValidator.IsValid(identifier))
                        {
                            TempData["ErrorMessage"] = "Lütfen geçerli bir e-posta adresi formatı giriniz!";
                            return RedirectToAction("Login", "Login");
                        }
                    }

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
                            TelefonNumarasi = p.TelefonNumarasi,
                            MailOnayliMi = p.MailOnayliMi // YENİ EKLENDİ
                        })
                        .FirstOrDefaultAsync();

                    if (personel != null)
                    {
                        // --- YENİ: MAİL ONAYI KONTROLÜ ---
                        if (personel.MailOnayliMi == 0)
                        {
                            TempData["ErrorMessage"] = "Hesabınız henüz doğrulanmamış. Lütfen e-postanıza gönderilen linke tıklayarak hesabınızı onaylayın.";
                            return RedirectToAction("Login", "Login");
                        }

                        bool logKayit = await LogKaydet(personel, "Sisteme Giriş Başarılı", "Login İşlemi", identifier);

                        if (logKayit)
                        {
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

        // --- YENİ: E-POSTA DOĞRULAMA ENDPOINT'İ ---
        [HttpGet]
        public async Task<IActionResult> VerifyEmail(string token)
        {
            try
            {
                if (string.IsNullOrEmpty(token))
                {
                    TempData["ErrorMessage"] = "Geçersiz doğrulama linki.";
                    return RedirectToAction("Login");
                }

                // Not: DB'de güncelleme yapacağımız için burada AsNoTracking KULLANMIYORUZ!
                var personel = await _context.Personels.FirstOrDefaultAsync(p => p.VerificationToken == token);

                if (personel == null)
                {
                    TempData["ErrorMessage"] = "Bu doğrulama kodu geçersiz veya daha önce kullanılmış.";
                    return RedirectToAction("Login");
                }

                // Doğrulama başarılı! Hesabı aktif et ve token'ı uçur.
                personel.MailOnayliMi = 1;
                personel.VerificationToken = null;

                await _context.SaveChangesAsync();

                // View tarafında bu SuccessMessage'ı yeşil bir alert ile göstermelisin.
                TempData["SuccessMessage"] = "E-posta adresiniz başarıyla doğrulandı! Şimdi giriş yapabilirsiniz.";
                return RedirectToAction("Login");
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

                var loginOlanPersonel = JsonSerializer.Deserialize<Personel>(personelJson);

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