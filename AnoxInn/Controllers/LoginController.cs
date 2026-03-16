using AxonInn.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using System;
using System.Threading.Tasks;

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
                return View();
            }
            catch (Exception ex)
            {
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
                    // DEĞİŞİKLİK 1: Sadece okuma yaptığımız için .AsNoTracking() ekledik (Hızlandırır)
                    var personel = await _context.Personels
                        .AsNoTracking()
                        .Include(p => p.DepartmanRefNavigation)
                        .FirstOrDefaultAsync(p => (p.MailAdresi == identifier || p.TelefonNumarasi == identifier)
                                               && p.Sifre == password
                                               && p.AktifMi == 1);

                    if (personel != null)
                    {
                        bool logKayit = await LogKaydet(personel, "Sisteme Giriş Başarılı", "Login İşlemi", identifier);

                        if (logKayit)
                        {
                            // DEĞİŞİKLİK 2: Ağır Entity Framework nesnesini değil,
                            // sadece diğer sayfalarda bize gerekecek olan verileri içeren HAFİF bir kopya oluşturduk.
                            // JSON boyutu %90 oranında küçülecek ve CPU rahatlayacak.
                            var sessionPersonel = new Personel
                            {
                                Id = personel.Id,
                                Adi = personel.Adi,
                                Soyadi = personel.Soyadi,
                                Yetki = personel.Yetki,
                                DepartmanRef = personel.DepartmanRef,
                                MailAdresi = personel.MailAdresi,
                                TelefonNumarasi = personel.TelefonNumarasi
                            };

                            HttpContext.Session.SetString("GirisYapanPersonel", JsonConvert.SerializeObject(sessionPersonel));

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

        // DEĞİŞİKLİK 3: LogKaydet içerisindeki veritabanı trafiği tek sorguya düşürüldü
        private async Task<bool> LogKaydet(Personel? personel, string islemTipi, string yeniDeger, string girilenVeri)
        {
            try
            {
                string hotelAdi = "";
                string departmanAdi = "";

                if (personel != null && personel.DepartmanRef != 0)
                {
                    // Hem departman adını hem de otel adını tek SQL sorgusu ile JOIN yaparak alıyoruz
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