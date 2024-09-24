using System.ComponentModel;

namespace API.Entities.Enums;

public enum PageType
{
    [Description("Story")]
    Story = 0,
    [Description("FrontCover")]
    FrontCover = 1,
    [Description("InnerCover")]
    InnerCover = 2,
    [Description("Roundup")]
    Roundup = 3,
    [Description("Advertisement")]
    Advertisement = 4,
    [Description("Editorial")]
    Editorial = 5,
    [Description("Letters")]
    Letters = 6,
    [Description("Preview")]
    Preview = 7,
    [Description("BackCover")]
    BackCover = 8,
    [Description("Other")]
    Other = 9,
    [Description("Deleted")]
    Deleted = 10
}