using System.Diagnostics;

namespace AxonInn.Models.SmsService
{
    public static class SmsSender
    {
        // 1. ADIM: Hesap açtığında firmanın sana vereceği bilgileri buraya gireceksin.
        // Aşağıdaki bilgiler temsilidir.
        private static readonly string ApiUrl = "https://api.netgsm.com.tr/sms/send/get"; // Örnek Netgsm GET API adresi
        private static readonly string ApiUsername = "kullanici_adin";
        private static readonly string ApiPassword = "sifren";
        private static readonly string MsgHeader = "AXONINN"; // Onaylı başlığın

        /// <summary>
        /// Belirtilen telefon numarasına gerçek SMS gönderir.
        /// Kullanım: bool sonuc = await SmsSender.SendSmsAsync("0532xxxxxxx", "Mesaj içeriği");
        /// </summary>
        public static async Task<bool> SendSmsAsync(string phoneNumber, string message)
        {
            if (string.IsNullOrWhiteSpace(phoneNumber) || string.IsNullOrWhiteSpace(message))
            {
                return false;
            }

            // Numarayı API'nin istediği formata (genelde başında 0 olmadan veya ülke koduyla) getirmek iyi bir pratiktir.
            // Bu örnekte numaranın temizlendiğini varsayıyoruz.

            try
            {
                using (HttpClient client = new HttpClient())
                {
                    // 2. ADIM: API'nin beklediği URL formatını oluşturuyoruz.
                    // Çoğu Türk firması basit GET veya POST istekleri kabul eder.
                    // Bu örnek GET isteği üzerindendir (Netgsm vb. firmaların dokümanlarına göre değişebilir).

                    // URL Encode işlemi, mesajdaki boşluk ve Türkçe karakterlerin (ş, ğ, ç vb.) bozulmamasını sağlar.
                    string encodedMessage = Uri.EscapeDataString(message);

                    string requestUrl = $"{ApiUrl}?usercode={ApiUsername}&password={ApiPassword}&gsmno={phoneNumber}&message={encodedMessage}&msgheader={MsgHeader}";

                    // 3. ADIM: İsteği gönderiyoruz
                    HttpResponseMessage response = await client.GetAsync(requestUrl);

                    // 4. ADIM: Sonucu kontrol ediyoruz
                    if (response.IsSuccessStatusCode)
                    {
                        string resultContent = await response.Content.ReadAsStringAsync();

                        // API'ler genelde başarılıysa "00" veya bir "Job ID" döner.
                        // Firmanın dokümanına göre buradaki kontrolü özelleştirebiliriz.
                        if (resultContent.StartsWith("00") || !resultContent.StartsWith("30")) // Örnek hata kodları
                        {
                            return true;
                        }
                    }
                    return false;
                }
            }
            catch (Exception ex)
            {
                // Hata durumunda loglama yapılabilir.
                Console.WriteLine($"SMS Gönderim Hatası: {ex.Message}");
                return false;
            }
        }
    }
}
