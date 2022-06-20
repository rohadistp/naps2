﻿using System.Reflection;

namespace NAPS2.Config;

public static class ConfigCopier
{
    private static readonly Dictionary<Type, (PropertyInfo, bool)[]> PropCache = new Dictionary<Type, (PropertyInfo, bool)[]>();

    public static void Copy<T>(T src, T dst)
    {
        if (src == null) throw new ArgumentNullException(nameof(src));
        if (dst == null) throw new ArgumentNullException(nameof(dst));
        Copy(src, dst, typeof(T));
    }

    private static void Copy(object src, object dst, Type t)
    {
        // TODO: Not actually cached?
        (PropertyInfo, bool)[] GetPropData() => t.GetProperties()
            .Select(x => (x, IsChildProp(x))).ToArray();

        foreach (var (prop, isChild) in PropCache.Get(t, GetPropData))
        {
            if (isChild)
            {
                var srcChild = prop.GetValue(src);
                if (srcChild != null)
                {
                    Copy(srcChild, prop.GetValue(dst), prop.PropertyType);
                }
            }
            else
            {
                var srcValue = prop.GetValue(src);
                if (srcValue != null)
                {
                    prop.SetValue(dst, srcValue);
                }
            }
        }
    }

    private static bool IsChildProp(PropertyInfo x)
    {
        return x.GetCustomAttributes().Any(y => y is ChildAttribute);
    }
}