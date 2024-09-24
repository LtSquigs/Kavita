﻿using System.Collections.Generic;
using System.IO;

namespace Skeleton.DTOs;

public class EmailOptionsDto
{
    public IList<string> ToEmails { get; set; }
    public string Subject { get; set; }
    public string Body { get; set; }
    public IList<KeyValuePair<string, string>> PlaceHolders { get; set; }
    /// <summary>
    /// Filenames to attach
    /// </summary>
    public IList<KeyValuePair<string, Stream>> Attachments { get; set; }
}