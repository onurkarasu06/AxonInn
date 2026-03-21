using AxonInn.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.Net.Mail; // YENİ: Yüksek hızlı mail format kontrolü için
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
        // 🛡️ GÜVENLİK 1: Brute-Force ve DDoS saldırılarına karşı hız sınırlayıcı (Rate Limiting).
        // Hacker'ların saniyede yüzlerce şifre deneyerek sunucu CPU'sunu (BCrypt yüzünden) kilitlemesini önler.
        [EnableRateLimiting("LoginLimit")]
        public async Task<ActionResult> Login(string email, string password)
        {
            try
            {
                if (!string.IsNullOrEmpty(email) && !string.IsNullOrEmpty(password))
                {
                    email = email.Trim();

                    // ⚡ CPU & RAM OPTİMİZASYONU: "new EmailAddressAttribute()" ile her girişte RAM'de nesne yaratmak (Allocation) yerine, 
                    // .NET'in allocation-free (bellek dostu) TryCreate metodu kullanıldı.
                    if (email.Contains("@") && !MailAddress.TryCreate(email, out _))
                    {
                        TempData["ErrorMessage"] = "Lütfen geçerli bir e-posta adresi formatı giriniz!";
                        return RedirectToAction("Login", "Login");
                    }

                    // ⚡ VERİTABANI AĞ (NETWORK) OPTİMİZASYONU:
                    // Log atarken ikinci bir SQL sorgusu oluşmasın diye Departman ve Hotel isimlerini aynı anda çekiyoruz. (Sıfır N+1 Problemi)
                    var dbSonuc = await _context.Personels
                                          .AsNoTracking()
                                          .Where(p => p.MailAdresi == email && p.AktifMi == 1)
                                          .Select(p => new
                                          {
                                              Personel = new Personel
                                              {
                                                  Id = p.Id,
                                                  Adi = p.Adi,
                                                  Soyadi = p.Soyadi,
                                                  Yetki = p.Yetki,
                                                  DepartmanRef = p.DepartmanRef,
                                                  MailAdresi = p.MailAdresi,
                                                  TelefonNumarasi = p.TelefonNumarasi,
                                                  MailOnayliMi = p.MailOnayliMi,
                                                  Sifre = p.Sifre
                                              },
                                              DepartmanAdi = p.DepartmanRefNavigation != null ? p.DepartmanRefNavigation.Adi : "",
                                              HotelAdi = (p.DepartmanRefNavigation != null && p.DepartmanRefNavigation.HotelRefNavigation != null) ? p.DepartmanRefNavigation.HotelRefNavigation.Adi : ""
                                          })
                                          .FirstOrDefaultAsync();

                    if (dbSonuc != null && dbSonuc.Personel != null && BCrypt.Net.BCrypt.Verify(password, dbSonuc.Personel.Sifre))
                    {
                        dbSonuc.Personel.Sifre = null;
                        var personel = dbSonuc.Personel;

                        if (personel.MailOnayliMi == 0)
                        {
                            TempData["ErrorMessage"] = "Hesabınız henüz doğrulanmamış. Lütfen e-postanıza gönderilen linke tıklayarak hesabınızı onaylayın.";
                            return RedirectToAction("Login", "Login");
                        }

                        // DB'den çektiğimiz hazır isimleri gönderiyoruz, LogKaydet bir daha veritabanına bağlanmıyor!
                        bool logKayit = await LogKaydet(personel, "Sisteme Giriş Başarılı", "Login İşlemi", email, dbSonuc.HotelAdi, dbSonuc.DepartmanAdi);

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
                        await LogKaydet(null, "Hatalı Giriş Denemesi", $"Kullanıcı Bulunamadı. Denenen: {email}", password);
                        TempData["ErrorMessage"] = "Girdiğiniz bilgiler hatalı veya kullanıcı aktif değil!";
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
        public async Task<IActionResult> VerifyEmail(string token)
        {
            try
            {
                if (string.IsNullOrEmpty(token))
                {
                    TempData["ErrorMessage"] = "Geçersiz doğrulama linki.";
                    return RedirectToAction("Login");
                }

                // ⚡ SIFIR RAM TÜKETİMİ (ExecuteUpdateAsync): Veriyi RAM'e almadan doğrudan Database üzerinde günceller.
                int etkilenenSatir = await _context.Personels
                    .Where(p => p.VerificationToken == token)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(p => p.MailOnayliMi, 1)
                        .SetProperty(p => p.VerificationToken, (string?)null));

                if (etkilenenSatir == 0)
                {
                    TempData["ErrorMessage"] = "Bu doğrulama kodu geçersiz veya daha önce kullanılmış.";
                    return RedirectToAction("Login");
                }

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

        // ⚡ N+1 KORUMASI: PreHotelAdi ve PreDeptAdi opsiyonel parametreleri eklendi.
        private async Task<bool> LogKaydet(Personel? personel, string islemTipi, string yeniDeger, string girilenVeri, string? preHotelAdi = null, string? preDeptAdi = null)
        {
            try
            {
                string hotelAdi = preHotelAdi ?? "";
                string departmanAdi = preDeptAdi ?? "";

                // Sadece ve sadece yukarıdan veri gönderilmediyse veritabanına bağlanır (Örneğin çıkış veya hatalı giriş işlemlerinde).
                if (personel != null && personel.DepartmanRef != 0 && string.IsNullOrEmpty(hotelAdi))
                {
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