namespace PhotoTools2.Models;

public sealed class PdfPageEdit
{
    public uint PageIndex { get; init; }
    public int RotationQuarterTurns { get; private set; }
    public bool Include { get; set; } = true;

    public int RotationDegrees => RotationQuarterTurns * 90;

    public void RotateLeft() => RotationQuarterTurns = (RotationQuarterTurns + 3) % 4;
    public void RotateRight() => RotationQuarterTurns = (RotationQuarterTurns + 1) % 4;
    public void ResetRotation() => RotationQuarterTurns = 0;
}
