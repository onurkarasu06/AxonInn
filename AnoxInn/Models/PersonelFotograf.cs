using System;
using System.Collections.Generic;

namespace AxonInn.Models;

public partial class PersonelFotograf
{
    public long Id { get; set; }

    public long PersonelRef { get; set; }

    public byte[] Fotograf { get; set; } = null!;

    public virtual Personel PersonelRefNavigation { get; set; } = null!;
}
