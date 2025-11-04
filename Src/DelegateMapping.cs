namespace GenerateDts;

public static class DelegateMapping
{
    public static bool IsDelegate(Type type)
    {
        var baseType = type.BaseType;
        while (baseType != null)
        {
            var baseName = baseType.FullName;
            if (baseName == "System.Delegate" || baseName == "System.MulticastDelegate")
            {
                return true;
            }
            baseType = baseType.BaseType;
        }
        return false;
    }

    public static string? MapDelegateToFunctionType(Type delegateType, Func<Type, string> mapType, Action<string> addWarning)
    {
        try
        {
            var invokeMethod = delegateType.GetMethod("Invoke");
            if (invokeMethod == null)
            {
                addWarning($"Delegate {delegateType.Name} has no Invoke method - mapped to 'any'");
                return "any";
            }

            var parameters = invokeMethod.GetParameters();
            var paramStrings = new List<string>();

            for (int i = 0; i < parameters.Length; i++)
            {
                var param = parameters[i];
                var paramName = string.IsNullOrEmpty(param.Name) ? $"arg{i}" : param.Name;
                var paramType = mapType(param.ParameterType);
                paramStrings.Add($"{paramName}: {paramType}");
            }

            var returnType = mapType(invokeMethod.ReturnType);
            var paramList = string.Join(", ", paramStrings);
            return $"({paramList}) => {returnType}";
        }
        catch (Exception ex)
        {
            addWarning($"Failed to map delegate {delegateType.Name}: {ex.Message}");
            return null;
        }
    }
}
