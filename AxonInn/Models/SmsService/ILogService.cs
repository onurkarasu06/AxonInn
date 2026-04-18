using AxonInn.Models.Entities;
using System.Threading.Tasks;

namespace AxonInn.Services
{
    public interface ILogService
    {
        Task<bool> LogKaydetAsync(Personel? personel, string islemTipi, string eskiDeger, string yeniDeger, string oncedenAlinanHotelAd = "", string oncedenAlinanDepartmanAd = "");
    }
}