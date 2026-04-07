using Microsoft.AspNetCore.Http;
using System;

namespace AxonInn.Helpers // Veya projendeki uygun bir namespace
{
    public static class FileSecurityExtensions
    {
        // 🛡️ GÜVENLİK 3: MIME Spoofing Kalkanı (Dosya İmzası Kontrolü) - ZERO ALLOCATION
        // "this" keyword'ü sayesinde IFormFile üzerinden doğrudan çağrılabilir.
        public static bool IsValidImageSignature(this IFormFile file)
        {
            // Dosya çok küçükse zaten resim olamaz
            if (file == null || file.Length < 12) return false;

            using var stream = file.OpenReadStream();

            // RAM'de allocation (çöp) yaratmamak için stack memory kullanıyoruz
            Span<byte> buffer = stackalloc byte[12];
            stream.Read(buffer);

            // ÇOK ÖNEMLİ: Stream okunduğu için imleç ilerledi. 
            // Daha sonra CopyToAsync ile kaydedebilmek için imleci tekrar başa sarıyoruz!
            stream.Position = 0;

            // 1. JPEG İmzası (FF D8 FF)
            if (buffer[0] == 0xFF && buffer[1] == 0xD8 && buffer[2] == 0xFF) return true;

            // 2. PNG İmzası (89 50 4E 47)
            if (buffer[0] == 0x89 && buffer[1] == 0x50 && buffer[2] == 0x4E && buffer[3] == 0x47) return true;

            // 3. GIF İmzası (GIF8) -> (47 49 46 38)
            if (buffer[0] == 0x47 && buffer[1] == 0x49 && buffer[2] == 0x46 && buffer[3] == 0x38) return true;

            // 4. WEBP İmzası (RIFF .... WEBP) -> (52 49 46 46 .... 57 45 42 50)
            if (buffer[0] == 0x52 && buffer[1] == 0x49 && buffer[2] == 0x46 && buffer[3] == 0x46 &&
                buffer[8] == 0x57 && buffer[9] == 0x45 && buffer[10] == 0x42 && buffer[11] == 0x50) return true;

            // Yukarıdaki standart imzalara uymuyorsa sahtedir veya desteklenmeyen formattır.
            return false;
        }
    }
}