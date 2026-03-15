using System;
using System.Collections.Generic;

namespace AxonInn.Models;

public partial class GorevFotograf
{
    public long Id { get; set; }

    public long GorevRef { get; set; }

    public byte[] Fotograf { get; set; } = null!;

    public virtual Gorev GorevRefNavigation { get; set; } = null!;
}
