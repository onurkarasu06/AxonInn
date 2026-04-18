using System;
using System.Collections.Generic;

namespace AxonInn.Models.Entities;

public partial class Departman
{
    public long Id { get; set; }

    public string Adi { get; set; } = null!;

    public long HotelRef { get; set; }

    // HATANIN ÇÖZÜMÜ: Departmanın bağlı olduğu Otel nesnesi
    public virtual Hotel HotelRefNavigation { get; set; } = null!;

    public virtual ICollection<Personel> Personels { get; set; } = new List<Personel>();
}