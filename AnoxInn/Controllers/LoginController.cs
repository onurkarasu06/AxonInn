using AxonInn.Models.Context;
using AxonInn.Models.Entities;
using AxonInn.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Net.Mail;
using System.Text.Json;
using System.Text.Json.Serialization; // ⚡ EKLENDİ: ReferenceHandler için gerekli

namespace AxonInn.Controllers
{
    [AutoValidateAntiforgeryToken]
    public class LoginController : Controller
    {
        private readonly AxonInnContext _context;
        private readonly IConfiguration _configuration;
        private readonly ILogService _logService;

        // ⚡ GÜVENLİK: JSON döngülerini (Reference Loop) engelleyen ayar eklendi
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReferenceHandler = ReferenceHandler.IgnoreCycles
        };

        // 🛠️ HATA 1 DÜZELTİLDİ: Çift constructor birleştirildi.
        public LoginController(AxonInnContext context, IConfiguration configuration, ILogService logService)
        {
            _context = context;
            _configuration = configuration;
            _logService = logService;
        }

        [HttpGet]
        public ActionResult Login()
        {
            try
            {
                var mevcutSession = HttpContext.Session.GetString("GirisYapanPersonel");
                if (!string.IsNullOrWhiteSpace(mevcutSession))
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
        [EnableRateLimiting("LoginLimit")]
        public async Task<ActionResult> Login(string email, string password)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
                {
                    TempData["ErrorMessage"] = "Lütfen Email/Telefon ve Şifre alanlarını doldurunuz!";
                    return RedirectToAction(nameof(Login));
                }

                email = email.Trim();

                if (email.Contains('@') && !MailAddress.TryCreate(email, out _))
                {
                    TempData["ErrorMessage"] = "Lütfen geçerli bir e-posta adresi formatı giriniz!";
                    return RedirectToAction(nameof(Login));
                }

                var dbSonuc = await _context.Personels
                  .AsNoTracking()
                  .Where(p => p.MailAdresi == email && p.AktifMi == 1)
                  .Select(p => new
                  {
                      p.Id,
                      p.Adi,
                      p.Soyadi,
                      p.AktifMi,
                      p.MedenHali,
                      p.Yetki,
                      p.DepartmanRef,
                      p.MailAdresi,
                      p.TelefonNumarasi,
                      p.MailOnayliMi,
                      p.Sifre,
                      p.DepartmanRefNavigation,
                      DepartmanAdi = p.DepartmanRefNavigation != null ? p.DepartmanRefNavigation.Adi : string.Empty,
                      HotelAdi = (p.DepartmanRefNavigation != null && p.DepartmanRefNavigation.HotelRefNavigation != null) ? p.DepartmanRefNavigation.HotelRefNavigation.Adi : string.Empty
                  })
                  .FirstOrDefaultAsync();

                if (dbSonuc != null && BCrypt.Net.BCrypt.Verify(password, dbSonuc.Sifre))
                {
                    if (dbSonuc.MailOnayliMi == 0)
                    {
                        TempData["ErrorMessage"] = "Hesabınız henüz doğrulanmamış. Lütfen e-postanıza gönderilen linke tıklayarak hesabınızı onaylayın.";
                        return RedirectToAction(nameof(Login));
                    }

                    var sessionPersonel = new Personel
                    {
                        Id = dbSonuc.Id,
                        Adi = dbSonuc.Adi,
                        Soyadi = dbSonuc.Soyadi,
                        AktifMi = dbSonuc.AktifMi,
                        MedenHali = dbSonuc.MedenHali,
                        Yetki = dbSonuc.Yetki,
                        DepartmanRef = dbSonuc.DepartmanRef,
                        MailAdresi = dbSonuc.MailAdresi,
                        TelefonNumarasi = dbSonuc.TelefonNumarasi,
                        MailOnayliMi = dbSonuc.MailOnayliMi,
                        DepartmanRefNavigation = dbSonuc.DepartmanRefNavigation
                    };

                    bool logKayit = await _logService.LogKaydetAsync(sessionPersonel, "Sisteme Giriş Başarılı", "Login İşlemi", email, dbSonuc.HotelAdi, dbSonuc.DepartmanAdi);

                    if (logKayit)
                    {
                        // 🛠️ HATA 2 DÜZELTİLDİ: Sonsuz döngüleri engelleyen options parametresi eklendi
                        HttpContext.Session.SetString("GirisYapanPersonel", JsonSerializer.Serialize(sessionPersonel, _jsonOptions));
                        return RedirectToAction("Ana", "Ana");
                    }

                    TempData["ErrorMessage"] = "Giriş Loglanamadı.";
                    return RedirectToAction(nameof(Login));
                }

                await _logService.LogKaydetAsync(null, "Hatalı Giriş Denemesi", $"Kullanıcı Bulunamadı. Denenen: {email}", string.Empty);
                TempData["ErrorMessage"] = "Girdiğiniz bilgiler hatalı veya kullanıcı aktif değil!";
                return RedirectToAction(nameof(Login));
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
                if (string.IsNullOrWhiteSpace(token))
                {
                    TempData["ErrorMessage"] = "Geçersiz doğrulama linki.";
                    return RedirectToAction(nameof(Login));
                }

                int etkilenenSatir = await _context.Personels
                    .Where(p => p.VerificationToken == token)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(p => p.MailOnayliMi, 1)
                        .SetProperty(p => p.VerificationToken, (string?)null));

                if (etkilenenSatir == 0)
                {
                    TempData["ErrorMessage"] = "Bu doğrulama kodu geçersiz veya daha önce kullanılmış.";
                    return RedirectToAction(nameof(Login));
                }

                TempData["SuccessMessage"] = "E-posta adresiniz başarıyla doğrulandı! Şimdi giriş yapabilirsiniz.";
                return RedirectToAction(nameof(Login));
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
                if (string.IsNullOrWhiteSpace(personelJson))
                    return RedirectToAction(nameof(Login));

                // 🛠️ HATA 2 DÜZELTİLDİ: Deserialize edilirken de options eklendi
                var loginOlanPersonel = JsonSerializer.Deserialize<Personel>(personelJson, _jsonOptions);

                bool logkayit = await _logService.LogKaydetAsync(loginOlanPersonel, "Güvenli Çıkış Yapıldı", "Logout İşlemi", loginOlanPersonel?.MailAdresi ?? string.Empty);

                if (logkayit)
                {
                    HttpContext.Session.Clear();
                    return RedirectToAction(nameof(Login));
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

        [HttpPost]
        [ValidateAntiForgeryToken]
        [EnableRateLimiting("LoginLimit")]
        public async Task<IActionResult> ForgotPassword(string email)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(email) || !MailAddress.TryCreate(email.Trim(), out _))
                {
                    TempData["ErrorMessage"] = "Geçerli bir e-posta adresi giriniz.";
                    return RedirectToAction(nameof(Login));
                }

                email = email.Trim();

                var userRecord = await _context.Personels
                    .AsNoTracking()
                    .Where(p => p.MailAdresi == email && p.AktifMi == 1)
                    .Select(p => new { p.Id, p.MailAdresi, p.MailOnayliMi, p.VerificationToken })
                    .FirstOrDefaultAsync();

                if (userRecord != null)
                {
                    if (userRecord.MailOnayliMi == 1 && string.IsNullOrWhiteSpace(userRecord.VerificationToken))
                    {
                        string newToken = Guid.NewGuid().ToString("N");

                        await _context.Personels
                            .Where(p => p.Id == userRecord.Id)
                            .ExecuteUpdateAsync(s => s
                                .SetProperty(p => p.VerificationToken, newToken)
                                .SetProperty(p => p.MailOnayliMi, 0));

                        bool mailBasariliMi = await SendPasswordResetEmailAsync(userRecord.MailAdresi, newToken);

                        if (mailBasariliMi)
                        {
                            await _logService.LogKaydetAsync(null, "Şifre Sıfırlama Maili Gönderildi.", "Başarılı", userRecord.MailAdresi);
                            TempData["SuccessMessage"] = $"{userRecord.MailAdresi} adresine şifre sıfırlama maili gönderildi.";
                        }
                        else
                        {
                            await _logService.LogKaydetAsync(null, "Şifre Sıfırlama Maili Gönderilemedi.", "Başarısız", userRecord.MailAdresi);
                            TempData["ErrorMessage"] = $"{userRecord.MailAdresi} adresine şifre sıfırlama maili gönderilemedi. Lütfen sistem yöneticisiyle iletişime geçin.";
                        }
                    }
                    else
                    {
                        await _logService.LogKaydetAsync(null, "Şifre değişikliğinden önce mail adresinizi aktive etmeniz gerekmektedir.", "Başarısız", userRecord.MailAdresi);
                        TempData["ErrorMessage"] = "Şifre Değişikliğinden Önce Mail Adresinizi Aktive Etmeniz Gerekmektedir.";
                    }
                }
                else
                {
                    TempData["SuccessMessage"] = "Eğer e-posta adresiniz sistemimizde kayıtlıysa, şifre sıfırlama bağlantısı gönderilmiştir.";
                }

                return RedirectToAction(nameof(Login));
            }
            catch (Exception ex)
            {
                return View("~/Views/Error/Error.cshtml", new ErrorViewModel { RequestId = ex.Message });
            }
        }

        private async Task<bool> SendPasswordResetEmailAsync(string? toEmail, string token)
        {
            if (string.IsNullOrWhiteSpace(toEmail)) return false;

            try
            {
                string smtpServer = _configuration["EmailSettings:SmtpServer"]!;
                int port = int.Parse(_configuration["EmailSettings:Port"] ?? "587");
                string senderEmail = _configuration["EmailSettings:SenderEmail"]!;
                string password = _configuration["EmailSettings:Password"]!;
                bool enableSsl = bool.Parse(_configuration["EmailSettings:EnableSsl"] ?? "false");

                string baseUrl = $"{Request.Scheme}://{Request.Host}";
                string resetLink = $"{baseUrl}/Login/ResetPassword?token={token}";

                string mailBody = $"""
            <div style='font-family: Arial, sans-serif; max-width: 600px; margin: auto; border: 1px solid #ddd; padding: 20px; border-radius: 10px;'>
                <h2 style='color: #2c3e50; text-align: center;'>AxonInn Şifre Sıfırlama</h2>
                <p style='font-size: 16px; color: #555;'>Merhaba,</p>
                <p style='font-size: 16px; color: #555;'>Hesabınızın şifresini sıfırlamak için bir talepte bulundunuz. İşleme devam etmek için lütfen aşağıdaki butona tıklayın:</p>
                <div style='text-align: center; margin: 30px 0;'>
                    <a href='{resetLink}' style='background-color: #e74c3c; color: white; padding: 12px 25px; text-decoration: none; border-radius: 5px; font-weight: bold; font-size: 16px;'>Şifremi Sıfırla</a>
                </div>
                <p style='font-size: 14px; color: #777;'>Eğer butona tıklayamıyorsanız, aşağıdaki linki kopyalayıp tarayıcınıza yapıştırabilirsiniz:</p>
                <p style='font-size: 12px; color: #3498db; word-break: break-all;'>{resetLink}</p>
                <p style='font-size: 14px; color: #777; margin-top:20px;'>Eğer bu talebi siz yapmadıysanız, hesabınız güvendedir. Bu e-postayı dikkate almayabilirsiniz.</p>
                <hr style='border: none; border-top: 1px solid #eee; margin-top: 30px;'/>
                <p style='font-size: 12px; color: #aaa; text-align: center;'>Bu e-posta otomatik olarak gönderilmiştir, lütfen cevaplamayınız.</p>
            </div>
            """;

                using var mailMessage = new MailMessage
                {
                    From = new MailAddress(senderEmail, "AxonInn Otomasyon"),
                    Subject = "AxonInn - Şifre Sıfırlama Talebi",
                    Body = mailBody,
                    IsBodyHtml = true,
                };

                mailMessage.To.Add(toEmail);

                using var smtpClient = new SmtpClient(smtpServer)
                {
                    Port = port,
                    Credentials = new NetworkCredential(senderEmail, password),
                    EnableSsl = enableSsl
                };

                await smtpClient.SendMailAsync(mailMessage);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Mail gönderme hatası: {ex.Message}");
                return false;
            }
        }

        [HttpGet]
        public async Task<IActionResult> ResetPassword(string token)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(token))
                {
                    TempData["ErrorMessage"] = "Geçersiz şifre sıfırlama bağlantısı.";
                    return RedirectToAction(nameof(Login));
                }

                bool tokenGecerli = await _context.Personels
                    .AnyAsync(p => p.VerificationToken == token && p.AktifMi == 1 && p.MailOnayliMi == 0);

                if (!tokenGecerli)
                {
                    TempData["ErrorMessage"] = "Şifre sıfırlama bağlantısı geçersiz veya süresi dolmuş.";
                    return RedirectToAction(nameof(Login));
                }

                TempData["ValidResetToken"] = token;
                return RedirectToAction(nameof(Login));
            }
            catch (Exception ex)
            {
                return View("~/Views/Error/Error.cshtml", new ErrorViewModel { RequestId = ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [EnableRateLimiting("LoginLimit")]
        public async Task<IActionResult> ResetPassword(string token, string newPassword, string confirmPassword)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(newPassword) || string.IsNullOrWhiteSpace(confirmPassword))
                {
                    TempData["ValidResetToken"] = token;
                    TempData["ErrorMessage"] = "Lütfen tüm alanları doldurunuz.";
                    return RedirectToAction(nameof(Login));
                }

                if (newPassword != confirmPassword)
                {
                    TempData["ValidResetToken"] = token;
                    TempData["ErrorMessage"] = "Girdiğiniz şifreler birbiriyle eşleşmiyor.";
                    return RedirectToAction(nameof(Login));
                }

                var userEmail = await _context.Personels
                    .AsNoTracking()
                    .Where(p => p.VerificationToken == token && p.AktifMi == 1)
                    .Select(p => p.MailAdresi)
                    .FirstOrDefaultAsync();

                if (string.IsNullOrWhiteSpace(userEmail))
                {
                    TempData["ErrorMessage"] = "Şifre sıfırlama işlemi başarısız. Lütfen tekrar deneyin.";
                    return RedirectToAction(nameof(Login));
                }

                string hashedSifre = BCrypt.Net.BCrypt.HashPassword(newPassword);

                await _context.Personels
                    .Where(p => p.VerificationToken == token && p.AktifMi == 1)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(p => p.Sifre, hashedSifre)
                        .SetProperty(p => p.VerificationToken, (string?)null)
                        .SetProperty(p => p.MailOnayliMi, 1));

                await _logService.LogKaydetAsync(null, "Şifre Başarıyla Sıfırlandı", "Başarılı", userEmail);

                TempData["SuccessMessage"] = "Şifreniz başarıyla değiştirildi! Yeni şifrenizle giriş yapabilirsiniz.";
                return RedirectToAction(nameof(Login));
            }
            catch (Exception ex)
            {
                return View("~/Views/Error/Error.cshtml", new ErrorViewModel { RequestId = ex.Message });
            }
        }
    }
}