

namespace ZhipuClient;

public class ImageInterpreterPool
{
    private readonly IEnumerable<ImageInterpreter> _imageInterpreters;
    public ImageInterpreterPool(params ImageInterpreter[] imageInterpreters)
    {
        _imageInterpreters = imageInterpreters;
    }
    public ImageInterpreterPool(IEnumerable<ImageInterpreter> imageInterpreters)
    {
        _imageInterpreters = imageInterpreters;
    }
    public async Task<string> Interpret(string imageUrl, ImageInterpreterType type = ImageInterpreterType.Normal)
    {
        return await InterpretCore(interpreter => interpreter.Interpret(imageUrl, type));
    }

    public async Task<string> Interpret(byte[] image, ImageInterpreterType type = ImageInterpreterType.Normal)
    {
        return await InterpretCore(interpreter => interpreter.Interpret(image, type));
    }

    async Task<string> InterpretCore(Func<ImageInterpreter, Task<string>> execute)
    {
        foreach (var interpreter in _imageInterpreters)
        {
            try
            {
                return await execute(interpreter);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error interpreting image with {interpreter.GetType().Name}: {ex.Message}");
            }
        }
        throw new Exception("No image interpreter available");
    }
}
