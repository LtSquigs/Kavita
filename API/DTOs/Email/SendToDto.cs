using System.Collections.Generic;
using System.IO;

namespace API.DTOs.Email;

public sealed record SendToDto
{
    public string DestinationEmail { get; set; } = default!;
    public IEnumerable<KeyValuePair<string, Stream>> FileStreams { get; set; } = default!;
}
