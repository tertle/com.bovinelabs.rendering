// <copyright file="VisibleIndex.cs" company="BovineLabs">
//     Copyright (c) BovineLabs. All rights reserved.
// </copyright>

namespace BovineLabs.Systems.Rendering
{
    using Unity.Entities;

    public struct VisibleIndex : IComponentData
    {
        public int Value;
    }
}