using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Net.Mail;
using System.Net;
using System.Text.Json;
using AxonInn.Models.Entities;
using AxonInn.Models.Context; // ⚡ Yüksek Hızlı Yeni Nesil JSON
using Microsoft.Extensions.Configuration; // ⚡ EKLENDİ: appsettings'i okumak için gerekli

namespace AxonInn.Controllers
{
    [AutoValidateAntiforgeryToken]
    public class IletisimController : Controller
    {
        private readonly AxonInnContext _context;
        private readonly IConfiguration _configuration; // ⚡ EKLENDİ

        // ⚡ CONSTRUCTOR GÜNCELLENDİ: IConfiguration Dependency Injection ile içeri alındı
        public IletisimController(AxonInnContext context, IConfiguration configuration)
        {
            _context = context;
            _configuration = configuration;
        }

        [Route("Iletisim")]
        public async Task<IActionResult> Iletisim()
        {
            try
            {
                var personelJson = HttpContext.Session.GetString("GirisYapanPersonel");
                if (string.IsNullOrEmpty(personelJson))
                    return RedirectToAction("Login", "Login");

                // ⚡ RAM OPTİMİZASYONU: Newtonsoft yerine System.Text.Json kullanıldı
                var loginOlanPersonel = JsonSerializer.Deserialize<Personel>(personelJson);

                // Sayfaya giriş logu
                await LogKaydet(loginOlanPersonel, "İletişim Sayfasına Giriş Yapıldı", "Sayfa Görüntüleme");

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
                var personelJson = HttpContext.Session.GetString("GirisYapanPersonel");
                if (string.IsNullOrEmpty(personelJson))
                    return RedirectToAction("Login", "Login");

                var loginOlanPersonel = JsonSerializer.Deserialize<Personel>(personelJson);

                // ⚡ Merkezi appsettings.json dosyasından SMTP ayarlarını çekiyoruz
                string smtpServer = _configuration["EmailSettings:SmtpServer"]!;
                int port = int.Parse(_configuration["EmailSettings:Port"] ?? "587");
                string senderEmail = _configuration["EmailSettings:SenderEmail"]!;
                string password = _configuration["EmailSettings:Password"]!;
                bool enableSsl = bool.Parse(_configuration["EmailSettings:EnableSsl"] ?? "false");

                // ⚡ RAM OPTİMİZASYONU (Memory Leak Önlemi): IDisposable nesneler "using var" ile anında bellekten temizlenir.
                using var mail = new MailMessage();
                mail.From = new MailAddress(senderEmail, "AxonInn Web Form"); // Dinamik yapıldı
                mail.To.Add("info@axoninn.com.tr"); // İstersen burayı da appsettings'ten bir "ReceiverEmail" olarak ayarlayabilirsin
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

                // Sabit IP yerine appsettings'ten gelen sunucu adresi (mail.axoninn.com.tr) kullanılıyor
                using var smtp = new SmtpClient(smtpServer, port);
                smtp.UseDefaultCredentials = false;
                smtp.Credentials = new NetworkCredential(senderEmail, password); // Dinamik şifre
                ServicePointManager.ServerCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) => true;
                smtp.EnableSsl = enableSsl; // Dinamik SSL

                // ⚡ THREAD (İŞLEMCİ) OPTİMİZASYONU: Asenkron SendMailAsync ile sunucunun kilitlenmesi önlendi.
                await smtp.SendMailAsync(mail);

                TempData["BasariMesaji"] = "Mesajınız başarıyla iletildi. En kısa sürede dönüş yapacağız.";

                // Başarılı gönderim logu
                await LogKaydet(loginOlanPersonel, "Mail Gönderildi", $"Konu: {model.Konu}");
            }
            catch (Exception ex)
            {
                TempData["HataMesaji"] = "Mail gönderimi sırasında bir hata oluştu. Lütfen daha sonra tekrar deneyin.";
                return View("~/Views/Error/Error.cshtml", new ErrorViewModel { RequestId = ex.Message });
            }

            return RedirectToAction("Iletisim");
        }

        private async Task<bool> LogKaydet(Personel? personel, string islemTipi, string yeniDeger)
        {
            try
            {
                string departmanAdi = personel?.DepartmanRefNavigation?.Adi ?? "";
                string hotelAdi = "";

                if (personel != null && personel.DepartmanRef != 0)
                {
                    // ⚡ DB OPTİMİZASYONU: Sadece okuma yapıldığı için AsNoTracking eklendi, RAM tasarrufu sağlandı.
                    hotelAdi = await _context.Departmen
                        .AsNoTracking()
                        .Where(d => d.Id == personel.DepartmanRef)
                        .Select(d => d.HotelRefNavigation.Adi)
                        .FirstOrDefaultAsync() ?? "";
                }

                var log = new AuditLog
                {
                    IslemTarihi = DateTime.Now,
                    IlgiliTablo = "Iletisim",
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