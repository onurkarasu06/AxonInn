using AxonInn.Models.Context;
using AxonInn.Models.Entities;
using AxonInn.Services;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text.Json.Serialization; // ⚡ EKLENDİ: ReferenceHandler için gerekli

namespace AxonInn.Controllers
{
    [AutoValidateAntiforgeryToken]
    public class GizlilikController : Controller
    {
        private readonly AxonInnContext _context;
        private readonly ILogService _logService;

        // ⚡ GÜVENLİK: JSON döngülerini (Reference Loop) engelleyen standart ayarımız
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReferenceHandler = ReferenceHandler.IgnoreCycles,
            PropertyNameCaseInsensitive = true // ⚡ EKLENDİ: Gelen JSON'daki büyük/küçük harf uyuşmazlığını tolere eder
        };

        // 🛠️ HATA 1 DÜZELTİLDİ: Tüm servisler tek constructor içinde birleştirildi
        public GizlilikController(AxonInnContext context, ILogService logService)
        {
            _context = context;
            _logService = logService;
        }

        [Route("Gizlilik")]
        public async Task<IActionResult> Gizlilik()
        {
            try
            {
                var personelJson = HttpContext.Session.GetString("GirisYapanPersonel");
                if (string.IsNullOrEmpty(personelJson))
                {
                    HttpContext.Session.Remove("GirisYapanPersonel"); // Döngüyü kırar
                    return RedirectToAction("Login", "Login");
                }

                // 🛠️ DÜZELTME: Session okunurken _jsonOptions parametresi eklendi
                var loginOlanPersonel = JsonSerializer.Deserialize<Personel>(personelJson, _jsonOptions);

                ViewData["GirisYapanPersonel"] = loginOlanPersonel;

                // 🛠️ HATA 2 DÜZELTİLDİ: Parametre sıralaması düzeltildi (eskiDeger: boş, yeniDeger: "Sayfa Görüntüleme")
                await _logService.LogKaydetAsync(loginOlanPersonel, "Gizlilik Sayfasına Giriş Yapıldı", string.Empty, "Sayfa Görüntüleme");

                return View("Gizlilik");
            }
            catch (Exception ex)
            {
                return View("~/Views/Error/Error.cshtml", new ErrorViewModel { RequestId = ex.Message });
            }
        }
    }
}