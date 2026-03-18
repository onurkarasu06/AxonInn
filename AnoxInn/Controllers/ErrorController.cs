using AxonInn.Models;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics; // ⚡ YENİ: RAM dostu hata izleme kimliği (TraceIdentifier) için eklendi.

namespace AxonInn.Controllers
{
    public class ErrorController : Controller
    {
        // ⚡ GÜVENLİK & PERFORMANS: Hata sayfalarının tarayıcı (Browser) tarafından önbelleğe alınmasını kesin olarak engeller.
        // Aksi takdirde kullanıcı sistem düzelse bile sürekli önbellekteki hata sayfasında (Cache Stuck) takılı kalır.
        [Route("Error")]
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error(ErrorViewModel? model)
        {
            // ⚡ PROTOKOL OPTİMİZASYONU: Ekranda 500 yazmasına rağmen arama motorları ve sunucular bu sayfayı 200 (Başarılı) algılamasın diye
            // Gerçek HTTP durum kodunu 500 olarak ayarlıyoruz.
            Response.StatusCode = 500;

            // ⚡ RAM OPTİMİZASYONU: "new ErrorViewModel" diyerek sürekli yeni bir nesne (Allocation) yaratmak yerine, 
            // C# Null-Coalescing (??=) operatörü ile mevcut nesnenin değerini set ederek Çöp Toplayıcı (GC) rahatlatıldı.
            model ??= new ErrorViewModel();

            // Eğer modelin RequestId'si boş gelirse, ASP.NET Core'un yerleşik TraceIdentifier (İzleme ID'si) özelliğini kullanarak 
            // sunucuda ekstra bir yük yaratmadan, güvenli ve teknik bir takip numarası atarız.
            if (string.IsNullOrWhiteSpace(model.RequestId))
            {
                model.RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier ?? "Bilinmeyen bir hata oluştu veya oturumunuz zaman aşımına uğradı.";
            }

            return View(model);
        }
    }
}