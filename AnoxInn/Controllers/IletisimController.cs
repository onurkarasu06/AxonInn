using AxonInn.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using System.Net.Mail;
using System.Text.Json.Serialization;

namespace AxonInn.Controllers
{
    public class IletisimController : Controller
    {
        private readonly AxonInnContext _context;

        public IletisimController(AxonInnContext context)
        {
            _context = context;
        }

        public async Task<IActionResult> Index()
        {
            try
            {
                var personelJson = HttpContext.Session.GetString("GirisYapanPersonel");
                if (string.IsNullOrEmpty(personelJson))
                    return RedirectToAction("Login", "Login");

                var loginOlanPersonel = JsonConvert.DeserializeObject<Personel>(personelJson);

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
        public async Task<IActionResult> MailGonder(Iletisim model) // Iletisim class'ının Models klasöründe olduğunu varsayıyoruz
        {
            try
            {
                var personelJson = HttpContext.Session.GetString("GirisYapanPersonel");
                if (string.IsNullOrEmpty(personelJson))
                    return RedirectToAction("Login", "Login");

                var loginOlanPersonel = JsonConvert.DeserializeObject<Personel>(personelJson);

                MailMessage mail = new MailMessage();
                mail.From = new MailAddress("no-reply@axoninn.com.tr", "AxonInn Web Form");
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

                SmtpClient smtp = new SmtpClient("104.247.162.18", 587);
                smtp.UseDefaultCredentials = false;
                smtp.Credentials = new System.Net.NetworkCredential("info@axoninn.com.tr", "12345+pl");
                System.Net.ServicePointManager.ServerCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) => true;
                smtp.EnableSsl = true;
                smtp.Send(mail);

                TempData["BasariMesaji"] = "Mesajınız başarıyla iletildi. En kısa sürede dönüş yapacağız.";

                // Başarılı gönderim logu
                await LogKaydet(loginOlanPersonel, "Mail Gönderildi", $"Konu: {model.Konu}");
            }
            catch (Exception ex)
            {
                TempData["HataMesaji"] = "Mail gönderimi sırasında bir hata oluştu. Lütfen daha sonra tekrar deneyin.";
                return View("~/Views/Error/Error.cshtml", new ErrorViewModel { RequestId = ex.Message });
            }

            return RedirectToAction("Index");
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
                    // DEĞİŞİKLİK: 2 ayrı veritabanı turu yerine Navigation Property ile tek sorguya (JOIN) düşürüldü.
                    hotelAdi = await _context.Departmen
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