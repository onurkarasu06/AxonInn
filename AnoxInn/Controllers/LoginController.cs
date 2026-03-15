using AxonInn.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using System;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace AxonInn.Controllers
{
    public class LoginController : Controller
    {
        private readonly AxonInnContext _context;

        // Dependency Injection ile DbContext'i alıyoruz
        public LoginController(AxonInnContext context)
        {
            _context = context;
        }

        [HttpGet]
        public ActionResult Login()
        {
            try
            {
                return View();
            }
            catch (Exception ex)
            {
                // Mevcut Error yapını korudum
                return View("~/Views/Error/Error.cshtml", new ErrorViewModel { RequestId = ex.Message });
            }
        }

        [HttpPost]
        public async Task<ActionResult> KullaniciGiris(string identifier, string password)
        {
            try
            {
                if (!string.IsNullOrEmpty(identifier) && !string.IsNullOrEmpty(password))
                {
                    // GetObject yerine EF Core ile doğrudan sorgulama yapıyoruz
                    var personel = await _context.Personels
                        .Include(p => p.DepartmanRefNavigation)
                        .FirstOrDefaultAsync(p => (p.MailAdresi == identifier || p.TelefonNumarasi == identifier)
                                               && p.Sifre == password
                                               && p.AktifMi == 1);

                    if (personel != null)
                    {
                        // Başarılı giriş logu
                        bool logKayit = await LogKaydet(personel, "Sisteme Giriş Başarılı", "Login İşlemi", identifier);

                        if (logKayit)
                        {
                            // Personel nesnesini Session'a JSON olarak atıyoruz (Döngüsel hatayı önlemek için Referansları yoksayarak)
                            var jsonSettings = new JsonSerializerSettings { ReferenceLoopHandling = ReferenceLoopHandling.Ignore };
                            HttpContext.Session.SetString("GirisYapanPersonel", JsonConvert.SerializeObject(personel, jsonSettings));

                            return RedirectToAction("Index", "Home");
                        }
                        else
                        {
                            TempData["ErrorMessage"] = "Giriş Loglanamadı.";
                            return RedirectToAction("Login", "Login");
                        }
                    }
                    else
                    {
                        // Başarısız giriş denemesi logu (Personel bulunamadığı için null gönderiyoruz)
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

                var loginOlanPersonel = JsonConvert.DeserializeObject<Personel>(personelJson);

                // Çıkış logunu kaydediyoruz
                bool logkayit = await LogKaydet(loginOlanPersonel, "Güvenli Çıkış Yapıldı", "Logout İşlemi", loginOlanPersonel.MailAdresi);

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

        // LogManager yerine EF Core tabanlı private log metodu
        private async Task<bool> LogKaydet(Personel? personel, string islemTipi, string yeniDeger, string girilenVeri)
        {
            try
            {
                string hotelAdi = "";
                string departmanAdi = "";

                if (personel != null && personel.DepartmanRefNavigation != null)
                {
                    departmanAdi = personel.DepartmanRefNavigation.Adi;
                    var hotel = await _context.Hotels.FirstOrDefaultAsync(h => h.Id == personel.DepartmanRefNavigation.HotelRef);
                    if (hotel != null) hotelAdi = hotel.Adi;
                }

                var log = new AuditLog
                {
                    IslemTarihi = DateTime.Now,
                    IlgiliTablo = "Personel",
                    KayitRefId = personel?.Id ?? 0,
                    IslemTipi = islemTipi,
                    EskiDeger = girilenVeri,
                    YeniDeger = yeniDeger,
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