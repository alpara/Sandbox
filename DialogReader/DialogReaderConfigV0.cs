using System.ComponentModel;

namespace X2T.BarcodeReader.Module.Hardware.DialogReader;

public class DialogReaderConfigV0 : BarcodeReaderBaseConfig
{
    [Category("Dialog Reader V0")]
    [DisplayName("Initial Pattern")]
    public string InitialPattern { get; set; } = "YYYYMMDDHHss";

    public DialogReaderConfigV0() : base(ReaderType.DialogV0) { }
}
