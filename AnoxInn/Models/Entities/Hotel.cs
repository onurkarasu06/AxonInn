using System;
using System.Collections.Generic;

namespace AxonInn.Models.Entities;

public partial class Hotel
{
    public long Id { get; set; }

    public string Adi { get; set; } = null!;

    // HATANIN ÇÖZÜMÜ: Otelin departmanlarını tutacak liste
    public virtual ICollection<Departman> Departmen { get; set; } = new List<Departman>();
}