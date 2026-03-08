namespace Elochka.App.Services;

internal interface ITextRecognitionService
{
    Task<string> RecognizeAsync(Bitmap bitmap, CancellationToken cancellationToken);
}
