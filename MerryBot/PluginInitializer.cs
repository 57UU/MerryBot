using BotPlugin;
using System.Reflection;

namespace MerryBot;
/// <summary>
/// use dependency injection to create instance
/// </summary>
internal class PluginInitializer<T>
{
    private static readonly NLog.Logger logger = NLog.LogManager.GetCurrentClassLogger();
    public PluginInitializer()
    {

    }
    private Dictionary<Type, List<object>> SpecificDependencies = new();

    private List<Edge> edges = new List<Edge>();
    private List<Type> nodes = new();
    public void AddDependency(Type type, List<object> specificDepency)
    {
        var constructors = type.GetConstructors();
        if (constructors.Length > 1)
        {
            //fault
            throw new Exception($"Type {type.Name} has too many contructors");
        }
        var constructor = constructors[0];
        var parameters = constructor.GetParameters();
        var existingDependency = specificDepency.Select(d => d.GetType()).ToList();
        foreach (var param in parameters)
        {
            var paramType = Nullable.GetUnderlyingType(param.ParameterType) ?? param.ParameterType;
            if (!existingDependency.Contains(paramType))
            {
                edges.Add(new Edge(type, paramType));
            }
        }
        this.SpecificDependencies.Add(type, specificDepency);
        this.nodes.Add(type);
    }
    private Dictionary<Type, T> instances = new();
    private List<T> initOrder = new();
    private object? _getInstance(Type requireType, Type currentType)
    {
        var actualType = Nullable.GetUnderlyingType(requireType) ?? requireType;
        var specificDependency = this.SpecificDependencies.GetValueOrDefault(currentType);
        if (specificDependency != null)
        {
            foreach (var i in specificDependency)
            {
                if (actualType.IsInstanceOfType(i))
                {
                    return i;
                }
            }
        }
        if (instances.TryGetValue(actualType, out var direct))
        {
            return direct;
        }
        return instances.Values.FirstOrDefault(candidate => actualType.IsInstanceOfType(candidate));

    }
    public T2? GetInstance<T2>() where T2 : T
    {
        return (T2)this.GetInstance(typeof(T2))!;
    }
    public T? GetInstance(Type type)
    {
        return instances.TryGetValue(type, out var direct)
            ? direct
            : instances.Values.FirstOrDefault(candidate => type.IsInstanceOfType(candidate));
    }
    /// <summary>
    /// get all dispose actions by inversed dependency order
    /// </summary>
    /// <returns></returns>
    public IEnumerable<Action> GetDisposeActions()
    {
        return initOrder
            .OfType<IDisposable>()
            .Reverse()
            .Select(d => (Action)(d.Dispose));
    }

    private bool IsNullableParameter(ParameterInfo param)
    {
        return Nullable.GetUnderlyingType(param.ParameterType) != null ||
               param.IsOptional ||
               param.HasDefaultValue;
    }

    private void Initialize(Type type)
    {
        try
        {
            ConstructorInfo constructor = type.GetConstructors()[0];
            List<object?> parameters = new();
            foreach (var i in constructor.GetParameters())
            {
                var p = _getInstance(i.ParameterType, type);
                if (p == null && !IsNullableParameter(i))
                {
                    throw new ChainException($"{type.Name} can't be loaded due to dependency {i.ParameterType.Name} is null");
                }
                parameters.Add(p);
            }
            var instance = constructor.Invoke(parameters.ToArray());
            instances[type] = (T)instance;
            initOrder.Add((T)instance);
        }
        catch (PluginNotUsableException ex)
        {
            logger.Warn($"the plugin {type.Name} can not be loaded, {ex.Message}");
        }
        catch (TargetInvocationException ex)
        {
            var inner = ex.InnerException;
            if (inner is PluginNotUsableException)
            {
                logger.Warn($"the plugin {type.Name} can not be loaded: {inner.Message}");
            }
            else
            {
                logger.Error(ex, $"the plugin {type.Name} can not be loaded");
            }
        }
        catch (ChainException ex)
        {
            logger.Error(ex.Message);
        }
        catch (Exception ex)
        {
            logger.Error(ex, $"the plugin {type.Name} can not be loaded");
        }
    }
    public void InitializeAll()
    {
        //calculate order
        int[] outDegree = new int[nodes.Count];
        Dictionary<Type, int> typeToIndex = new();
        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            typeToIndex.Add(node, i);
        }
        ;
        List<int>[] dependencies = new List<int>[nodes.Count];// a,b : b depend on a
        foreach (var edge in edges)
        {
            var source = typeToIndex[edge.source];
            if (!typeToIndex.TryGetValue(edge.target, out var target))
            {
                var implementations = nodes
                    .Select((node, index) => (node, index))
                    .Where(candidate => edge.target.IsAssignableFrom(candidate.node))
                    .ToList();
                if (implementations.Count != 1)
                {
                    throw new ChainException(
                        $"{edge.source.Name} 依赖 {edge.target.Name}，但找到 {implementations.Count} 个可用实现");
                }
                target = implementations[0].index;
            }
            if (dependencies[target] == null)
            {
                dependencies[target] = [];
            }
            dependencies[target].Add(source);
            outDegree[source]++;
        }
        Queue<int> queue = new();
        for (int i = 0; i < outDegree.Length; i++)
        {
            if (outDegree[i] == 0)
            {
                //ok
                queue.Enqueue(i);
            }
        }
        while (queue.Count > 0)
        {
            var currentItem = queue.Dequeue();
            Initialize(nodes[currentItem]);
            var currentDependency = dependencies[currentItem];
            if (currentDependency != null)
            {
                foreach (var source in currentDependency)
                {
                    outDegree[source]--;
                    if (outDegree[source] == 0)
                    {
                        queue.Enqueue(source);
                    }
                }
            }

        }
        //verify

        //find item which out-degreee != 0
        List<Type> types = new();
        for (var i = 0; i < outDegree.Length; i++)
        {
            if (outDegree[i] != 0)
            {
                types.Add(nodes[i]);
            }
        }
        if (types.Count != 0)
        {
            logger.Warn($"loop detect: {string.Join(",", types.Select(i => i.Name))}");
        }


        //ok
    }
    /// <summary>
    /// 依赖关系: source depend on target
    /// </summary>
    private record Edge(
        Type source,
        Type target
        );
    private class ChainException : Exception
    {
        public ChainException(string message) : base(message) { }
    }
}
