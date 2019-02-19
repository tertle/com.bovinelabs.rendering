// <copyright file="Extensions.cs" company="BovineLabs">
//     Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Systems.Rendering
{
    using System;

    public static class Extensions
    {
        public static bool ImplementsInterface(this Type type, Type i)
        {
            var interfaceTypes = type.GetInterfaces();

            if (i.IsGenericTypeDefinition)
            {
                foreach (var interfaceType in interfaceTypes)
                {
                    if (interfaceType.IsGenericType && interfaceType.GetGenericTypeDefinition() == i)
                    {
                        return true;
                    }
                }
            }
            else
            {
                foreach (var interfaceType in interfaceTypes)
                {
                    if (interfaceType == i)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}