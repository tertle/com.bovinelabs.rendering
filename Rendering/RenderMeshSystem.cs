﻿// <copyright file="WorldInitialization.cs" company="BovineLabs">
//     Copyright (c) BovineLabs. All rights reserved.
// </copyright>
// <auto-generated>It's not, just don't want analyzers working on this file.</auto-generated>
namespace BovineLabs.Systems.Rendering
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using Unity.Burst;
    using Unity.Collections;
    using Unity.Collections.LowLevel.Unsafe;
    using Unity.Entities;
    using Unity.Jobs;
    using Unity.Mathematics;
    using Unity.Rendering;
    using Unity.Transforms;
    using UnityEngine;
    using UnityEngine.Assertions;
    using UnityEngine.Profiling;
    using FrustumPlanes = Unity.Rendering.FrustumPlanes;

    /// <summary>
    /// Renders all Entities containing both RenderMesh & LocalToWorld components.
    /// </summary>
    [ExecuteAlways]
    [UpdateAfter(typeof(RenderBoundsUpdateSystem))]
    public class RenderMeshSystem : ComponentSystem
    {
        public static readonly Dictionary<Camera, Material> ReplacementMaterials = new Dictionary<Camera, Material>();

        private int[] globalToLocalScd = new int[0];
        private readonly List<RenderMesh> renderMeshes = new List<RenderMesh>();
        private readonly List<int> renderMeshesComponentIndices = new List<int>();
        private readonly MaterialPropertyBlock propertyBlock = new MaterialPropertyBlock();

        private InstancedRenderPropertyBase[] instancedProperties = Array.Empty<InstancedRenderPropertyBase>();
        private readonly List<InstancedRenderPropertyBase> supportedInstancedProperties = new List<InstancedRenderPropertyBase>();
        private readonly Dictionary<EntityArchetype, List<InstancedRenderPropertyBase>> archetypeInstancedProperties = new Dictionary<EntityArchetype, List<InstancedRenderPropertyBase>>();

        public Camera ActiveCamera;

        private int m_LastFrozenChunksOrderVersion = -1;
        private int m_LastDynamicChunksOrderVersion = -1;
        private int m_LastLocalToWorldOrderVersion = -1;

        private NativeArray<ArchetypeChunk> m_FrozenChunks;
        private NativeArray<ArchetypeChunk> m_DynamicChunks;
        private NativeArray<WorldRenderBounds> m_FrozenChunkBounds;

        // Instance renderer takes only batches of 1023
        Matrix4x4[] m_MatricesArray = new Matrix4x4[1023];
        private NativeArray<float4> m_Planes;

        ComponentGroup m_FrozenChunksQuery;
        ComponentGroup m_DynamicChunksQuery;

        static unsafe void CopyTo(NativeSlice<VisibleLocalToWorld> transforms, int count, Matrix4x4[] outMatrices, int offset)
        {
            // @TODO: This is using unsafe code because the Unity DrawInstances API takes a Matrix4x4[] instead of NativeArray.
            Assert.AreEqual(sizeof(Matrix4x4), sizeof(VisibleLocalToWorld));
            fixed (Matrix4x4* resultMatrices = outMatrices)
            {
                VisibleLocalToWorld* sourceMatrices = (VisibleLocalToWorld*)transforms.GetUnsafeReadOnlyPtr();
                UnsafeUtility.MemCpy(resultMatrices + offset, sourceMatrices,
                    UnsafeUtility.SizeOf<Matrix4x4>() * count);
            }
        }

        /// <inheritdoc />
        protected override void OnCreateManager()
        {
            this.FetchProperties();

            this.m_FrozenChunksQuery = this.GetComponentGroup(new EntityArchetypeQuery
            {
                Any = Array.Empty<ComponentType>(),
                None = Array.Empty<ComponentType>(),
                All = new ComponentType[] { typeof(LocalToWorld), typeof(RenderMesh), typeof(VisibleLocalToWorld), typeof(Frozen) },
            });

            this.m_DynamicChunksQuery = this.GetComponentGroup(new EntityArchetypeQuery
            {
                Any = Array.Empty<ComponentType>(),
                None = new ComponentType[] { typeof(Frozen) },
                All = new ComponentType[] { typeof(LocalToWorld), typeof(RenderMesh), typeof(VisibleLocalToWorld) },
            });

            this.GetComponentGroup(new EntityArchetypeQuery
            {
                Any = Array.Empty<ComponentType>(),
                None = new ComponentType[] { typeof(VisibleLocalToWorld) },
                All = new ComponentType[] { typeof(RenderMesh), typeof(LocalToWorld) },
            });

            this.m_Planes = new NativeArray<float4>(6, Allocator.Persistent);
        }

        /// <inheritdoc />
        protected override void OnDestroyManager()
        {
            if (this.m_FrozenChunks.IsCreated)
            {
                this.m_FrozenChunks.Dispose();
            }

            if (this.m_FrozenChunkBounds.IsCreated)
            {
                this.m_FrozenChunkBounds.Dispose();
            }

            if (this.m_DynamicChunks.IsCreated)
            {
                this.m_DynamicChunks.Dispose();
            }

            this.m_Planes.Dispose();
        }

        [BurstCompile]
        struct UpdateChunkBounds : IJobParallelFor
        {
            [ReadOnly]
            public NativeArray<ArchetypeChunk> Chunks;
            [ReadOnly]
            public ArchetypeChunkComponentType<WorldRenderBounds> WorldRenderBoundsType;
            public NativeArray<WorldRenderBounds> ChunkBounds;

            public void Execute(int index)
            {
                var chunk = this.Chunks[index];

                var instanceBounds = chunk.GetNativeArray(this.WorldRenderBoundsType);
                if (instanceBounds.Length == 0)
                    return;

                // TODO: Improve this approach
                // See: https://www.inf.ethz.ch/personal/emo/DoctThesisFiles/fischer05.pdf

                var chunkBounds = (Bounds)instanceBounds[0].Value;
                for (int j = 1; j < instanceBounds.Length; j++)
                {
                    chunkBounds.Encapsulate(instanceBounds[j].Value);
                }

                this.ChunkBounds[index] = new WorldRenderBounds { Value = chunkBounds };
            }

        }

        [BurstCompile]
        unsafe struct CullLODToVisible : IJobParallelFor
        {
            [ReadOnly]
            public NativeArray<ArchetypeChunk> Chunks;
            [ReadOnly]
            public ComponentDataFromEntity<ActiveLODGroupMask> ActiveLODGroupMask;
            [ReadOnly]
            public ArchetypeChunkComponentType<MeshLODComponent> MeshLODComponentType;
            [ReadOnly] public ArchetypeChunkComponentType<LocalToWorld> LocalToWorldType;
            [ReadOnly]
            public ArchetypeChunkComponentType<WorldRenderBounds> WorldRenderBoundsType;
            [NativeDisableUnsafePtrRestriction]
            [ReadOnly]
            public WorldRenderBounds* ChunkBounds;
            [ReadOnly]
            public NativeArray<float4> Planes;
            public ArchetypeChunkComponentType<VisibleLocalToWorld> VisibleLocalToWorldType;
            [NativeDisableParallelForRestriction]
            public ArchetypeChunkComponentType<VisibleIndex> VisibleIndexType;
            public NativeArray<int> ChunkVisibleCount;

            float4x4* GetVisibleOutputBuffer(ArchetypeChunk chunk)
            {
                var chunkVisibleLocalToWorld = chunk.GetNativeArray(this.VisibleLocalToWorldType);
                return (float4x4*)chunkVisibleLocalToWorld.GetUnsafePtr();
            }

            float4x4* GetLocalToWorldSourceBuffer(ArchetypeChunk chunk)
            {
                var chunkLocalToWorld = chunk.GetNativeArray(this.LocalToWorldType);

                if (chunkLocalToWorld.Length > 0)
                    return (float4x4*)chunkLocalToWorld.GetUnsafeReadOnlyPtr();
                else
                    return null;
            }

            void VisibleIn(int index)
            {
                var chunk = this.Chunks[index];
                var chunkEntityCount = chunk.Count;
                var chunkVisibleCount = 0;
                var chunkLODs = chunk.GetNativeArray(this.MeshLODComponentType);
                var hasMeshLODComponentType = chunkLODs.Length > 0;

                float4x4* dstPtr = this.GetVisibleOutputBuffer(chunk);
                float4x4* srcPtr = this.GetLocalToWorldSourceBuffer(chunk);
                if (srcPtr == null)
                    return;

                var chunkVisibleLocalToWorld = chunk.GetNativeArray(this.VisibleIndexType);

                if (!hasMeshLODComponentType)
                {
                    for (int i = 0; i < chunkEntityCount; i++)
                    {
                        UnsafeUtility.MemCpy(dstPtr + chunkVisibleCount + i, srcPtr + i, UnsafeUtility.SizeOf<float4x4>());
                        chunkVisibleLocalToWorld[chunkVisibleCount + i] = new VisibleIndex { Value = i };
                    }

                    chunkVisibleCount = chunkEntityCount;
                }
                else
                {
                    for (int i = 0; i < chunkEntityCount; i++)
                    {
                        var instanceLOD = chunkLODs[i];
                        var instanceLODValid =
                            (this.ActiveLODGroupMask[instanceLOD.Group].LODMask & instanceLOD.LODMask) != 0;
                        if (instanceLODValid)
                        {
                            UnsafeUtility.MemCpy(dstPtr + chunkVisibleCount, srcPtr + i, UnsafeUtility.SizeOf<float4x4>());
                            chunkVisibleLocalToWorld[chunkVisibleCount] = new VisibleIndex { Value = i };
                            chunkVisibleCount++;
                        }
                    }
                }

                this.ChunkVisibleCount[index] = chunkVisibleCount;
            }

            void VisiblePartial(int index)
            {
                var chunk = this.Chunks[index];
                var chunkEntityCount = chunk.Count;
                var chunkVisibleCount = 0;
                var chunkLODs = chunk.GetNativeArray(this.MeshLODComponentType);
                var chunkBounds = chunk.GetNativeArray(this.WorldRenderBoundsType);
                var hasMeshLODComponentType = chunkLODs.Length > 0;
                var hasWorldRenderBounds = chunkBounds.Length > 0;

                var chunkVisibleLocalToWorld = chunk.GetNativeArray(this.VisibleIndexType);

                float4x4* dstPtr = this.GetVisibleOutputBuffer(chunk);
                float4x4* srcPtr = this.GetLocalToWorldSourceBuffer(chunk);
                if (srcPtr == null)
                    return;

                // 00 (-WorldRenderBounds -MeshLODComponentType)
                if ((!hasWorldRenderBounds) && (!hasMeshLODComponentType))
                {
                    for (int i = 0; i < chunkEntityCount; i++)
                    {
                        UnsafeUtility.MemCpy(dstPtr + chunkVisibleCount + i, srcPtr + i, UnsafeUtility.SizeOf<float4x4>());
                        chunkVisibleLocalToWorld[chunkVisibleCount + i] = new VisibleIndex { Value = i };
                    }

                    chunkVisibleCount = chunkEntityCount;
                }
                // 01 (-WorldRenderBounds +MeshLODComponentType)
                else if ((!hasWorldRenderBounds) && (hasMeshLODComponentType))
                {
                    for (int i = 0; i < chunkEntityCount; i++)
                    {
                        var instanceLOD = chunkLODs[i];
                        var instanceLODValid =
                            (this.ActiveLODGroupMask[instanceLOD.Group].LODMask & instanceLOD.LODMask) != 0;
                        if (instanceLODValid)
                        {
                            UnsafeUtility.MemCpy(dstPtr + chunkVisibleCount, srcPtr + i, UnsafeUtility.SizeOf<float4x4>());
                            chunkVisibleLocalToWorld[chunkVisibleCount] = new VisibleIndex { Value = i };
                            chunkVisibleCount++;
                        }
                    }
                }
                // 10 (+WorldRenderBounds -MeshLODComponentType)
                else if ((hasWorldRenderBounds) && (!hasMeshLODComponentType))
                {
                    for (int i = 0; i < chunkEntityCount; i++)
                    {
                        var instanceBounds = chunkBounds[i];
                        var instanceCullValid = (FrustumPlanes.Intersect(this.Planes, instanceBounds.Value) !=
                                                 FrustumPlanes.IntersectResult.Out);

                        if (instanceCullValid)
                        {
                            UnsafeUtility.MemCpy(dstPtr + chunkVisibleCount, srcPtr + i, UnsafeUtility.SizeOf<float4x4>());
                            chunkVisibleLocalToWorld[chunkVisibleCount] = new VisibleIndex { Value = i };
                            chunkVisibleCount++;
                        }
                    }
                }
                // 11 (+WorldRenderBounds +MeshLODComponentType)
                else
                {

                    for (int i = 0; i < chunkEntityCount; i++)
                    {
                        var instanceLOD = chunkLODs[i];
                        var instanceLODValid =
                            (this.ActiveLODGroupMask[instanceLOD.Group].LODMask & instanceLOD.LODMask) != 0;
                        if (instanceLODValid)
                        {
                            var instanceBounds = chunkBounds[i];
                            var instanceCullValid = (FrustumPlanes.Intersect(this.Planes, instanceBounds.Value) !=
                                                     FrustumPlanes.IntersectResult.Out);
                            if (instanceCullValid)
                            {
                                UnsafeUtility.MemCpy(dstPtr + chunkVisibleCount, srcPtr + i, UnsafeUtility.SizeOf<float4x4>());
                                chunkVisibleLocalToWorld[chunkVisibleCount] = new VisibleIndex { Value = i };
                                chunkVisibleCount++;
                            }
                        }
                    }
                }

                this.ChunkVisibleCount[index] = chunkVisibleCount;
            }

            public void Execute(int index)
            {
                if (this.ChunkBounds == null)
                {
                    this.VisiblePartial(index);
                    return;
                }

                var chunk = this.Chunks[index];

                var hasWorldRenderBounds = chunk.Has(this.WorldRenderBoundsType);
                if (!hasWorldRenderBounds)
                {
                    this.VisibleIn(index);
                    return;
                }

                var chunkBounds = this.ChunkBounds[index];
                var chunkInsideResult = FrustumPlanes.Intersect(this.Planes, chunkBounds.Value);
                if (chunkInsideResult == FrustumPlanes.IntersectResult.Out)
                {
                    this.ChunkVisibleCount[index] = 0;
                }
                else if (chunkInsideResult == FrustumPlanes.IntersectResult.In)
                {
                    this.VisibleIn(index);
                }
                else
                {
                    this.VisiblePartial(index);
                }
            }
        }
        
        [BurstCompile]
        struct MapChunkRenderers : IJobParallelFor
        {
            [ReadOnly]
            public NativeArray<ArchetypeChunk> Chunks;
            [ReadOnly]
            public ArchetypeChunkSharedComponentType<RenderMesh> RenderMeshType;
            public NativeMultiHashMap<int, int>.Concurrent ChunkRendererMap;

            public void Execute(int index)
            {
                var chunk = this.Chunks[index];
                var rendererSharedComponentIndex = chunk.GetSharedComponentIndex(this.RenderMeshType);
                this.ChunkRendererMap.Add(rendererSharedComponentIndex, index);
            }
        }

        [BurstCompile]
        struct GatherSortedChunks : IJob
        {
            [ReadOnly]
            public NativeMultiHashMap<int, int> ChunkRendererMap;
            public int SharedComponentCount;
            public NativeArray<ArchetypeChunk> SortedChunks;
            public NativeArray<ArchetypeChunk> Chunks;

            public void Execute()
            {
                int sortedIndex = 0;
                for (int i = 0; i < this.SharedComponentCount; i++)
                {
                    int chunkIndex = 0;

                    NativeMultiHashMapIterator<int> it;
                    if (!this.ChunkRendererMap.TryGetFirstValue(i, out chunkIndex, out it))
                        continue;
                    do
                    {
                        this.SortedChunks[sortedIndex] = this.Chunks[chunkIndex];
                        sortedIndex++;
                    } while (this.ChunkRendererMap.TryGetNextValue(out chunkIndex, ref it));
                }
            }
        }

        [BurstCompile]
        unsafe struct PackVisibleChunkIndices : IJob
        {
            [ReadOnly]
            public NativeArray<ArchetypeChunk> Chunks;
            [ReadOnly]
            public NativeArray<int> ChunkVisibleCount;
            public NativeArray<int> PackedChunkIndices;
            [NativeDisableUnsafePtrRestriction]
            public int* PackedChunkCount;

            public void Execute()
            {
                var packedChunkCount = 0;
                for (int i = 0; i < this.Chunks.Length; i++)
                {
                    if (this.ChunkVisibleCount[i] > 0)
                    {
                        this.PackedChunkIndices[packedChunkCount] = i;
                        packedChunkCount++;
                    }
                }

                *this.PackedChunkCount = packedChunkCount;
            }

        }

        unsafe void UpdateFrozenInstanceRenderer()
        {
            if (this.m_FrozenChunks.Length == 0)
            {
                return;
            }
            this.UpdateInstanceRenderers(this.m_FrozenChunks, (WorldRenderBounds*)this.m_FrozenChunkBounds.GetUnsafePtr());
        }

        unsafe void UpdateDynamicInstanceRenderer()
        {
            if (this.m_DynamicChunks.Length == 0)
            {
                return;
            }

            this.UpdateInstanceRenderers(this.m_DynamicChunks, null);
        }

        private unsafe void UpdateInstanceRenderers(NativeArray<ArchetypeChunk> chunks, WorldRenderBounds* worldRenderBounds)
        {
            Profiler.BeginSample("Gather Types");
            var localToWorldType = this.GetArchetypeChunkComponentType<LocalToWorld>(true);
            var visibleLocalToWorldType = this.GetArchetypeChunkComponentType<VisibleLocalToWorld>();
            var visibleIndexType = this.GetArchetypeChunkComponentType<VisibleIndex>();
            var worldRenderBoundsType = this.GetArchetypeChunkComponentType<WorldRenderBounds>(true);
            var meshLODComponentType = this.GetArchetypeChunkComponentType<MeshLODComponent>(true);
            var activeLODGroupMask = this.GetComponentDataFromEntity<ActiveLODGroupMask>(true);
            var renderMeshType = this.GetArchetypeChunkSharedComponentType<RenderMesh>();
            var flippedWindingTagType = this.GetArchetypeChunkComponentType<RenderMeshFlippedWindingTag>();

            Profiler.EndSample();

            Profiler.BeginSample("Allocate Temp Data");
            var chunkVisibleCount = new NativeArray<int>(chunks.Length, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var packedChunkIndices = new NativeArray<int>(chunks.Length, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            Profiler.EndSample();

            var cullLODToVisibleJob = new CullLODToVisible
            {
                Chunks = chunks,
                ActiveLODGroupMask = activeLODGroupMask,
                MeshLODComponentType = meshLODComponentType,
                WorldRenderBoundsType = worldRenderBoundsType,
                VisibleIndexType = visibleIndexType,
                ChunkBounds = worldRenderBounds,
                Planes = this.m_Planes,
                ChunkVisibleCount = chunkVisibleCount,
                LocalToWorldType = localToWorldType,
                VisibleLocalToWorldType = visibleLocalToWorldType,
            };
            var cullLODToVisibleJobHandle = cullLODToVisibleJob.Schedule(chunks.Length, 64);

            var packedChunkCount = 0;
            var packVisibleChunkIndicesJob = new PackVisibleChunkIndices
            {
                Chunks = chunks,
                ChunkVisibleCount = chunkVisibleCount,
                PackedChunkIndices = packedChunkIndices,
                PackedChunkCount = &packedChunkCount,
            };
            var packVisibleChunkIndicesJobHandle = packVisibleChunkIndicesJob.Schedule(cullLODToVisibleJobHandle);
            packVisibleChunkIndicesJobHandle.Complete();

            Profiler.BeginSample("Process DrawMeshInstanced");
            var drawCount = 0;
            var lastRendererIndex = -1;
            var batchCount = 0;
            var flippedWinding = false;

            for (int i = 0; i < packedChunkCount; i++)
            {
                var chunkIndex = packedChunkIndices[i];
                var chunk = chunks[chunkIndex];
                var rendererIndex = chunk.GetSharedComponentIndex(renderMeshType);
                var activeCount = chunkVisibleCount[chunkIndex];
                var rendererChanged = rendererIndex != lastRendererIndex;
                var fullBatch = (batchCount + activeCount) > 1023;
                var visibleTransforms = chunk.GetNativeArray(visibleLocalToWorldType);
                var indices = chunk.GetNativeArray(visibleIndexType);

                var newFlippedWinding = chunk.Has(flippedWindingTagType);

                if ((fullBatch || rendererChanged || (newFlippedWinding != flippedWinding)) && (batchCount > 0))
                {
                    this.RenderBatch(lastRendererIndex, batchCount);

                    drawCount++;
                    batchCount = 0;
                }

                this.GenerateMaterialPropertyBlocks(chunk, indices, rendererIndex, rendererChanged, batchCount, activeCount);

                CopyTo(visibleTransforms, activeCount, this.m_MatricesArray, batchCount);

                flippedWinding = newFlippedWinding;
                batchCount += activeCount;
                lastRendererIndex = rendererIndex;
            }

            if (batchCount > 0)
            {
                this.RenderBatch(lastRendererIndex, batchCount);

                drawCount++;
            }

            Profiler.EndSample();

            packedChunkIndices.Dispose();
            chunkVisibleCount.Dispose();
        }

        private void GenerateMaterialPropertyBlocks(ArchetypeChunk chunk, NativeArray<VisibleIndex> indices, int rendererIndex, bool rendererChanged, int count, int offset)
        {
            if (rendererChanged)
            {
                var currentRenderer = this.renderMeshes[this.globalToLocalScd[rendererIndex]];

                // todo: cache supported properties per material
                this.supportedInstancedProperties.Clear();
                for (var p = 0; p < this.instancedProperties.Length; p++)
                {
                    if (this.instancedProperties[p].FetchDefault(currentRenderer.material))
                    {
                        this.supportedInstancedProperties.Add(this.instancedProperties[p]);
                    }
                }
            }

            // get properties in this chunk
            if (!this.archetypeInstancedProperties.TryGetValue(chunk.Archetype, out var atInstancedProperties))
            {
                atInstancedProperties = this.archetypeInstancedProperties[chunk.Archetype] = this.GetChunkProperties(chunk);
            }

            for (var p = 0; p < atInstancedProperties.Count; p++)
            {
                if (this.supportedInstancedProperties.Contains(atInstancedProperties[p]))
                {
                    atInstancedProperties[p].AddData(chunk, indices, count, offset); // TODO
                }
            }
        }

        private List<InstancedRenderPropertyBase> GetChunkProperties(in ArchetypeChunk chunk)
        {
            var atProperties = new List<InstancedRenderPropertyBase>();
            for (int i = 0; i < this.instancedProperties.Length; i++)
            {
                if (this.instancedProperties[i].Exists(chunk))
                {
                    atProperties.Add(this.instancedProperties[i]);
                }
            }

            return atProperties;
        }

        private void FetchProperties()
        {
            // find property types
            var instancedPropertyTypes = new List<Type>();
            foreach (var ass in AppDomain.CurrentDomain.GetAssemblies())
            {
                IEnumerable<Type> allTypes;
                try
                {
                    allTypes = ass.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    allTypes = e.Types.Where(t => t != null);
                }

                instancedPropertyTypes.AddRange(allTypes.Where(t => t.IsValueType && t.ImplementsInterface(typeof(IInstancedRenderProperty<>))));
            }

            this.CreateInstancedPropertyWrappers(instancedPropertyTypes);
        }

        private void CreateInstancedPropertyWrappers(IReadOnlyList<Type> propertyTypes)
        {
            this.instancedProperties = new InstancedRenderPropertyBase[propertyTypes.Count];
            for (var i = 0; i < propertyTypes.Count; i++)
            {
                var propertyInterface = propertyTypes[i].GetInterfaces().First(
                    t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IInstancedRenderProperty<>));
                var cargoType = propertyInterface.GenericTypeArguments[0];

                var wrapType = GetInstancedWrapType(cargoType);
                var constructor = wrapType.MakeGenericType(propertyTypes[i]).GetConstructor(Array.Empty<Type>());
                Debug.Assert(constructor != null, nameof(constructor) + " != null");
                var wrap = constructor.Invoke(Array.Empty<object>()) as InstancedRenderPropertyBase;
                this.instancedProperties[i] = wrap;
            }
        }

        private static Type GetInstancedWrapType(Type cargoType)
        {
            // todo: fetch using reflection? InstancedRenderProperty<,> with second parameter == cargoType
            //       Should nearly never change thought...
            if (cargoType == typeof(float))
            {
                return typeof(FloatInstancedRenderProperty<>);
            }

            if (cargoType == typeof(float4))
            {
                return typeof(Float4InstancedRenderProperty<>);
            }

            if (cargoType == typeof(float4x4))
            {
                return typeof(Float4x4InstancedRenderProperty<>);
            }

            throw new ArgumentException($"Invalid cargo for render property {cargoType.Name}");
        }

        void RenderBatch(int lastRendererIndex, int batchCount)
        {
            var renderer = this.renderMeshes[this.globalToLocalScd[lastRendererIndex]];

            if (renderer.mesh && renderer.material)
            {
                if (renderer.material.enableInstancing)
                {
                    for (var i = 0; i < this.supportedInstancedProperties.Count; i++)
                    {
                        this.supportedInstancedProperties[i].ApplyAndFree(this.propertyBlock, batchCount);
                    }

                    Graphics.DrawMeshInstanced(renderer.mesh, renderer.subMesh, renderer.material, this.m_MatricesArray,
                        batchCount, this.propertyBlock, renderer.castShadows, renderer.receiveShadows, renderer.layer, this.ActiveCamera);
                }
                else
                {
                    for (int i = 0; i != batchCount; i++)
                    {
                        Graphics.DrawMesh(renderer.mesh, this.m_MatricesArray[i], renderer.material, renderer.layer, this.ActiveCamera, renderer.subMesh, null, renderer.castShadows, renderer.receiveShadows);
                    }

                    //@TODO : temporarily disabled because it spams the console about Resources/unity_builtin_extra
                    //@TODO : also, it doesn't work in the player because of AssetDatabase
                    //                    if (batchCount >= 2)
                    //                        Debug.LogWarning($"Please enable GPU instancing for better performance ({renderer.material})\n{AssetDatabase.GetAssetPath(renderer.material)}", renderer.material);
                }
            }

            this.propertyBlock.Clear();
        }

        void UpdateFrozenChunkCache()
        {
            var visibleLocalToWorldOrderVersion = this.EntityManager.GetComponentOrderVersion<VisibleLocalToWorld>();
            var frozenOrderVersion = this.EntityManager.GetComponentOrderVersion<Frozen>();
            var staticChunksOrderVersion = math.min(visibleLocalToWorldOrderVersion, frozenOrderVersion);
            if (staticChunksOrderVersion == this.m_LastFrozenChunksOrderVersion)
                return;

            // Dispose
            if (this.m_FrozenChunks.IsCreated)
            {
                this.m_FrozenChunks.Dispose();
            }
            if (this.m_FrozenChunkBounds.IsCreated)
            {
                this.m_FrozenChunkBounds.Dispose();
            }

            var sharedComponentCount = this.EntityManager.GetSharedComponentCount();
            var RenderMeshType = this.GetArchetypeChunkSharedComponentType<RenderMesh>();
            var WorldRenderBoundsType = this.GetArchetypeChunkComponentType<WorldRenderBounds>(true);

            // Allocate temp data
            var chunkRendererMap = new NativeMultiHashMap<int, int>(100000, Allocator.TempJob);
            var foundArchetypes = new NativeList<EntityArchetype>(Allocator.TempJob);

            Profiler.BeginSample("CreateArchetypeChunkArray");
            var chunks = this.m_FrozenChunksQuery.CreateArchetypeChunkArray(Allocator.TempJob);
            Profiler.EndSample();

            this.m_FrozenChunks = new NativeArray<ArchetypeChunk>(chunks.Length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            this.m_FrozenChunkBounds = new NativeArray<WorldRenderBounds>(chunks.Length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

            var mapChunkRenderersJob = new MapChunkRenderers
            {
                Chunks = chunks,
                RenderMeshType = RenderMeshType,
                ChunkRendererMap = chunkRendererMap.ToConcurrent()
            };
            var mapChunkRenderersJobHandle = mapChunkRenderersJob.Schedule(chunks.Length, 64);

            var gatherSortedChunksJob = new GatherSortedChunks
            {
                ChunkRendererMap = chunkRendererMap,
                SharedComponentCount = sharedComponentCount,
                SortedChunks = this.m_FrozenChunks,
                Chunks = chunks
            };
            var gatherSortedChunksJobHandle = gatherSortedChunksJob.Schedule(mapChunkRenderersJobHandle);

            var updateChangedChunkBoundsJob = new UpdateChunkBounds
            {
                Chunks = this.m_FrozenChunks,
                WorldRenderBoundsType = WorldRenderBoundsType,
                ChunkBounds = this.m_FrozenChunkBounds
            };
            var updateChangedChunkBoundsJobHandle = updateChangedChunkBoundsJob.Schedule(chunks.Length, 64, gatherSortedChunksJobHandle);
            updateChangedChunkBoundsJobHandle.Complete();

            foundArchetypes.Dispose();
            chunkRendererMap.Dispose();
            chunks.Dispose();

            this.m_LastFrozenChunksOrderVersion = staticChunksOrderVersion;
        }

        void UpdateDynamicChunkCache()
        {
            var dynamicChunksOrderVersion = this.EntityManager.GetComponentOrderVersion<VisibleLocalToWorld>();
            if (dynamicChunksOrderVersion == this.m_LastDynamicChunksOrderVersion)
                return;

            // Dispose
            if (this.m_DynamicChunks.IsCreated)
            {
                this.m_DynamicChunks.Dispose();
            }

            var sharedComponentCount = this.EntityManager.GetSharedComponentCount();
            var RenderMeshType = this.GetArchetypeChunkSharedComponentType<RenderMesh>();

            // Allocate temp data
            var chunkRendererMap = new NativeMultiHashMap<int, int>(100000, Allocator.TempJob);
            var foundArchetypes = new NativeList<EntityArchetype>(Allocator.TempJob);

            Profiler.BeginSample("CreateArchetypeChunkArray");
            var chunks = this.m_DynamicChunksQuery.CreateArchetypeChunkArray(Allocator.TempJob);
            Profiler.EndSample();

            this.m_DynamicChunks = new NativeArray<ArchetypeChunk>(chunks.Length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

            var mapChunkRenderersJob = new MapChunkRenderers
            {
                Chunks = chunks,
                RenderMeshType = RenderMeshType,
                ChunkRendererMap = chunkRendererMap.ToConcurrent()
            };
            var mapChunkRenderersJobHandle = mapChunkRenderersJob.Schedule(chunks.Length, 64);

            var gatherSortedChunksJob = new GatherSortedChunks
            {
                ChunkRendererMap = chunkRendererMap,
                SharedComponentCount = sharedComponentCount,
                SortedChunks = this.m_DynamicChunks,
                Chunks = chunks
            };
            var gatherSortedChunksJobHandle = gatherSortedChunksJob.Schedule(mapChunkRenderersJobHandle);
            gatherSortedChunksJobHandle.Complete();

            foundArchetypes.Dispose();
            chunkRendererMap.Dispose();
            chunks.Dispose();

            this.m_LastDynamicChunksOrderVersion = dynamicChunksOrderVersion;
        }

        void UpdateMissingVisibleLocalToWorld()
        {
            var localToWorldOrderVersion = this.EntityManager.GetComponentOrderVersion<LocalToWorld>();
            if (localToWorldOrderVersion == this.m_LastLocalToWorldOrderVersion)
                return;

            EntityCommandBuffer entityCommandBuffer = new EntityCommandBuffer(Allocator.Temp);

            var query = new EntityArchetypeQuery
            {
                Any = Array.Empty<ComponentType>(),
                None = new ComponentType[] { typeof(VisibleLocalToWorld) },
                All = new ComponentType[] { typeof(RenderMesh), typeof(LocalToWorld) }
            };
            var entityType = this.GetArchetypeChunkEntityType();
            var chunks = this.EntityManager.CreateArchetypeChunkArray(query, Allocator.TempJob);
            for (int i = 0; i < chunks.Length; i++)
            {
                var chunk = chunks[i];
                var entities = chunk.GetNativeArray(entityType);
                for (int j = 0; j < chunk.Count; j++)
                {
                    var entity = entities[j];
                    entityCommandBuffer.AddComponent(entity, default(VisibleLocalToWorld));
                    entityCommandBuffer.AddComponent(entity, default(VisibleIndex));
                }
            }

            entityCommandBuffer.Playback(this.EntityManager);
            entityCommandBuffer.Dispose();
            chunks.Dispose();

            this.m_LastLocalToWorldOrderVersion = localToWorldOrderVersion;
        }

        private void PrepareMeshes()
        {
            this.renderMeshes.Clear();
            this.renderMeshesComponentIndices.Clear();
            this.EntityManager.GetAllUniqueSharedComponentData(this.renderMeshes, this.renderMeshesComponentIndices);

            Array.Resize(ref this.globalToLocalScd, math.max(this.globalToLocalScd.Length, this.EntityManager.GetSharedComponentCount()));

            for (var i = 0; i < this.renderMeshesComponentIndices.Count; i++)
            {
                this.globalToLocalScd[this.renderMeshesComponentIndices[i]] = i;
            }

            if (ReplacementMaterials.TryGetValue(this.ActiveCamera, out var material))
            {
                for (int i = 0; i < this.renderMeshes.Count; i++)
                {
                    var instance = this.renderMeshes[i];
                    instance.material = material;
                    this.renderMeshes[i] = instance;
                }
            }
        }

        protected override void OnUpdate()
        {
            if (this.ActiveCamera != null)
            {
                FrustumPlanes.FromCamera(this.ActiveCamera, this.m_Planes);

                this.UpdateMissingVisibleLocalToWorld();

                for (var i = 0; i < this.instancedProperties.Length; i++)
                {
                    this.instancedProperties[i].FetchArchetypeType(this);
                }

                this.PrepareMeshes();

                Profiler.BeginSample("UpdateFrozenChunkCache");
                this.UpdateFrozenChunkCache();
                Profiler.EndSample();

                Profiler.BeginSample("UpdateDynamicChunkCache");
                this.UpdateDynamicChunkCache();
                Profiler.EndSample();

                Profiler.BeginSample("UpdateFrozenInstanceRenderer");
                this.UpdateFrozenInstanceRenderer();
                Profiler.EndSample();

                Profiler.BeginSample("UpdateDynamicInstanceRenderer");
                this.UpdateDynamicInstanceRenderer();
                Profiler.EndSample();
            }
        }
    }
}