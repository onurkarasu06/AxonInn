using AxonInn.Models;
using Microsoft.AspNetCore.Mvc;

namespace AxonInn.Controllers
{
    public class ErrorController : Controller
    {
        // Gelen hatayı yakalayıp View'a gönderen metot
        [Route("Error")]
        public IActionResult Error(ErrorViewModel model)
        {
            // Eğer boş gelirse varsayılan bir değer atıyoruz
            if (model == null || string.IsNullOrEmpty(model.RequestId))
            {
                model = new ErrorViewModel { RequestId = "Bilinmeyen bir hata oluştu." };
            }
            return View(model);
        }

        // Hata sayfasından çıkış yapmak için (GET metodu daha kullanışlıdır)
        [HttpGet]
        public IActionResult SafeLogout()
        {
            HttpContext.Session.Clear();
            return RedirectToAction("Login", "Login");
        }
    }
}