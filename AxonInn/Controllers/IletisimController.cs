using AxonInn.Models.Context;
using AxonInn.Models.Entities;
using AxonInn.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Net.Mail;
using System.Text.Json;
using System.Text.Json.Serialization; // ⚡ EKLENDİ: ReferenceHandler için gerekli

namespace AxonInn.Controllers
{
    [AutoValidateAntiforgeryToken]
    public class IletisimController : Controller
    {
        private readonly AxonInnContext _context;
        private readonly IConfiguration _configuration;
        private readonly ILogService _logService;
        private readonly ICurrentUserService _currentUserService;

        // ⚡ GÜVENLİK: JSON döngülerini (Reference Loop) engelleyen ayar eklendi
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReferenceHandler = ReferenceHandler.IgnoreCycles,
            PropertyNameCaseInsensitive = true // ⚡ EKLENDİ: Gelen JSON'daki büyük/küçük harf uyuşmazlığını tolere eder
        };


        // 🛠️ HATA 1 DÜZELTİLDİ: Çift constructor birleştirildi. Tüm DI nesneleri tek kurucuda.
        public IletisimController(AxonInnContext context, IConfiguration configuration, ILogService logService, ICurrentUserService currentUserService)
        {
            _context = context;
            _configuration = configuration;
            _logService = logService;
            _currentUserService = currentUserService;
        }

        [Route("Iletisim")]
        public async Task<IActionResult> Iletisim()
        {
            try
            {
                var loginOlanPersonel = _currentUserService.GetUser();
                if (loginOlanPersonel == null) return RedirectToAction("Login", "Login");

                // 🛠️ HATA 2 DÜZELTİLDİ: Parametre sıralaması (eskiDeger: boş, yeniDeger: "Sayfa Görüntüleme") yapıldı
                await _logService.LogKaydetAsync(loginOlanPersonel, "İletişim Sayfasına Giriş Yapıldı", string.Empty, "Sayfa Görüntüleme");

                return View("Iletisim", loginOlanPersonel);
            }
            catch (Exception ex)
            {
                return View("~/Views/Error/Error.cshtml", new ErrorViewModel { RequestId = ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken] // ⚡ GÜVENLİK: Kötü niyetli dış bot form gönderimlerini (CSRF) engeller
        public async Task<IActionResult> MailGonder(Iletisim model)
        {
            try
            {
                var loginOlanPersonel = _currentUserService.GetUser();
                if (loginOlanPersonel == null) return RedirectToAction("Login", "Login");

                string smtpServer = _configuration["EmailSettings:SmtpServer"]!;
                int port = int.Parse(_configuration["EmailSettings:Port"] ?? "587");
                string senderEmail = _configuration["EmailSettings:SenderEmail"]!;
                string password = _configuration["EmailSettings:Password"]!;
                bool enableSsl = bool.Parse(_configuration["EmailSettings:EnableSsl"] ?? "false");

                using var mail = new MailMessage();
                mail.From = new MailAddress(senderEmail, "AxonInn Web Form");
                mail.To.Add("info@axoninn.com.tr");
                mail.Subject = $"Yeni Mesaj: {model.Konu}";
                mail.IsBodyHtml = true;
                mail.Body = $@"
                    <div style='font-family: Arial, sans-serif; padding: 20px;'>
                        <h3 style='color: #0d6efd;'>Yeni İletişim Formu Mesajı</h3>
                        <p><strong>Gönderen:</strong> {model.AdSoyad}</p>
                        <p><strong>E-Posta:</strong> {model.Email}</p>
                        <p><strong>Konu:</strong> {model.Konu}</p>
                        <hr style='border: 1px solid #eee;'>
                        <p><strong>Mesaj Detayı:</strong><br>{model.Mesaj}</p>
                    </div>
                ";

                using var smtp = new SmtpClient(smtpServer, port);
                smtp.UseDefaultCredentials = false;
                smtp.Credentials = new NetworkCredential(senderEmail, password);
                smtp.EnableSsl = enableSsl;

                await smtp.SendMailAsync(mail);

                TempData["BasariMesaji"] = "Mesajınız başarıyla iletildi. En kısa sürede dönüş yapacağız.";

                // 🛠️ HATA 2 DÜZELTİLDİ: Tüm form modelini JSON olarak yeniDeger parametresine kaydettik.
                string jsonModel = JsonSerializer.Serialize(model, _jsonOptions);
                await _logService.LogKaydetAsync(loginOlanPersonel, "İletişim Formu Dolduruldu ve Mail Gönderildi", string.Empty, jsonModel);
            }
            catch (Exception ex)
            {
                TempData["HataMesaji"] = "Mail gönderimi sırasında bir hata oluştu. Lütfen daha sonra tekrar deneyin.";
                return View("~/Views/Error/Error.cshtml", new ErrorViewModel { RequestId = ex.Message });
            }

            return RedirectToAction("Iletisim");
        }
    }
}