

namespace ZhipuClient;

public class ImageInterpreterPool
{
    private readonly IEnumerable<ImageInterpreter> _imageInterpreters;
    public ImageInterpreterPool(params ImageInterpreter[] imageInterpreters)
    {
        _imageInterpreters=imageInterpreters;
    }
    public ImageInterpreterPool(IEnumerable<ImageInterpreter> imageInterpreters)
    {
        _imageInterpreters=imageInterpreters;
    }
    public async Task<string> Interpret(string imageUrl)
    {
        foreach (var interpreter in _imageInterpreters)
        {
            try
            {
                return await interpreter.Interpret(imageUrl);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error interpreting image with {interpreter.GetType().Name}: {ex.Message}");
            }
        }
        throw new Exception("No image interpreter available");
    }
}