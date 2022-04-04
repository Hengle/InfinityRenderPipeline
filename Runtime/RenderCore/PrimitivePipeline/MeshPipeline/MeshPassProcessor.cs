using Unity.Jobs;
using UnityEngine;
using Unity.Collections;
using UnityEngine.Rendering;
using InfinityTech.Core.Native;
using InfinityTech.Rendering.RDG;
using System.Runtime.InteropServices;
using InfinityTech.Rendering.Pipeline;
using InfinityTech.Rendering.GPUResource;
using UnityEngine.Rendering.RendererUtils;

namespace InfinityTech.Rendering.MeshPipeline
{
    public struct FMeshPassDescriptor
    {
        public int renderQueueMin;
        public int renderQueueMax;
        public int renderLayerMask;
        public bool excludeMotionVectorObjects;

        public FMeshPassDescriptor(in RendererListDesc rendererListDesc)
        {
            renderLayerMask = (int)rendererListDesc.layerMask;
            renderQueueMin = rendererListDesc.renderQueueRange.lowerBound;
            renderQueueMax = rendererListDesc.renderQueueRange.upperBound;
            excludeMotionVectorObjects = rendererListDesc.excludeObjectMotionVectors;
        }

        public FMeshPassDescriptor(in int minQueue, in int maxQueue)
        {
            renderLayerMask = 0;
            renderQueueMin = minQueue;
            renderQueueMax = maxQueue;
            excludeMotionVectorObjects = false;
        }
    }

    public class FMeshPassProcessor
    {
        private FGPUScene m_GPUScene;
        private ProfilingSampler m_DrawProfiler;
        private NativeArray<int> m_MeshBatchIndexs;
        private MaterialPropertyBlock m_PropertyBlock;
        private NativeList<JobHandle> m_MeshPassTaskRefs;
        private NativeList<FPassMeshSection> m_PassMeshSections;
        private NativeList<FMeshDrawCommand> m_MeshDrawCommands;

        public FMeshPassProcessor(FGPUScene gpuScene, ref NativeList<JobHandle> meshPassTaskRefs)
        {
            m_GPUScene = gpuScene;
            m_DrawProfiler = new ProfilingSampler("RenderLoop.DrawMeshBatcher");
            m_PropertyBlock = new MaterialPropertyBlock();
            m_MeshPassTaskRefs = meshPassTaskRefs;
        }

        internal void DispatchSetup(in FCullingData cullingData, in FMeshPassDescriptor meshPassDescriptor)
        {
            if (m_GPUScene.meshElements.IsCreated == false || cullingData.viewMeshElements.IsCreated == false || cullingData.isSceneView != true) { return; }
            if (cullingData.viewMeshElements.Length == 0) { return; }

            m_MeshBatchIndexs = new NativeArray<int>(cullingData.viewMeshElements.Length, Allocator.TempJob);
            m_PassMeshSections = new NativeList<FPassMeshSection>(cullingData.viewMeshElements.Length, Allocator.TempJob);
            m_MeshDrawCommands = new NativeList<FMeshDrawCommand>(cullingData.viewMeshElements.Length, Allocator.TempJob);

            FMeshPassFilterJob meshPassFilterJob;
            meshPassFilterJob.cullingData = cullingData;
            meshPassFilterJob.meshElements = m_GPUScene.meshElements;
            meshPassFilterJob.passMeshSections = m_PassMeshSections;
            meshPassFilterJob.meshPassDescriptor = meshPassDescriptor;
            JobHandle filterHandle = meshPassFilterJob.Schedule();

            FMeshPassSortJob meshPassSortJob;
            meshPassSortJob.passMeshSections = m_PassMeshSections;
            JobHandle sortHandle = meshPassSortJob.Schedule(filterHandle);

            FMeshPassBuildJob meshPassBuildJob;
            meshPassBuildJob.meshElements = m_GPUScene.meshElements;
            meshPassBuildJob.meshBatchIndexs = m_MeshBatchIndexs;
            meshPassBuildJob.meshDrawCommands = m_MeshDrawCommands;
            meshPassBuildJob.passMeshSections = m_PassMeshSections;
            m_MeshPassTaskRefs.Add(meshPassBuildJob.Schedule(sortHandle));
        }

        internal void DispatchDraw(in FRDGContext graphContext, in int passIndex)
        {
            if (!m_MeshBatchIndexs.IsCreated && !m_PassMeshSections.IsCreated && !m_MeshDrawCommands.IsCreated) { return; }

            using (new ProfilingScope(graphContext.cmdBuffer, m_DrawProfiler))
            {
                FBufferRef bufferRef = graphContext.resourcePool.GetBuffer(new FBufferDescriptor(10000, Marshal.SizeOf(typeof(int))));
                graphContext.cmdBuffer.SetBufferData(bufferRef.buffer, m_MeshBatchIndexs);

                for (int i = 0; i < m_MeshDrawCommands.Length; ++i)
                {
                    FMeshDrawCommand meshDrawCommand = m_MeshDrawCommands[i];
                    Mesh mesh = graphContext.world.meshAssets.Get(meshDrawCommand.meshIndex);
                    Material material = graphContext.world.materialAssets.Get(meshDrawCommand.materialIndex);

                    m_PropertyBlock.SetInt(InfinityShaderIDs.MeshBatchOffset, meshDrawCommand.countOffset.y);
                    m_PropertyBlock.SetBuffer(InfinityShaderIDs.MeshBatchIndexs, bufferRef.buffer);
                    m_PropertyBlock.SetBuffer(InfinityShaderIDs.MeshBatchBuffer, m_GPUScene.bufferRef.buffer);
                    graphContext.cmdBuffer.DrawMeshInstancedProcedural(mesh, meshDrawCommand.sectionIndex, material, passIndex, meshDrawCommand.countOffset.x, m_PropertyBlock);
                }

                graphContext.resourcePool.ReleaseBuffer(bufferRef);
            }

            m_MeshBatchIndexs.Dispose();
            m_PassMeshSections.Dispose();
            m_MeshDrawCommands.Dispose();
        }
    }
}
