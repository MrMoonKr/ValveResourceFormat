using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using GUI.Utils;
using OpenTK.Graphics.OpenGL;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization;
using ValveResourceFormat.Utils;

namespace GUI.Types.Renderer
{
    class RenderableMesh
    {
        public AABB BoundingBox { get; }
        public Vector4 Tint { get; set; } = Vector4.One;

        private readonly Scene scene;
        private readonly VrfGuiContext guiContext;
        public List<DrawCall> DrawCallsOpaque { get; } = new List<DrawCall>();
        public List<DrawCall> DrawCallsBlended { get; } = new List<DrawCall>();
        public int? AnimationTexture { get; private set; }
        public int AnimationTextureSize { get; private set; }

        public int MeshIndex { get; }

        private readonly Mesh mesh;
        private readonly VBIB VBIB;
        private readonly List<DrawCall> DrawCallsAll = new();

        public RenderableMesh(Mesh mesh, int meshIndex, Scene scene,
            Dictionary<string, string> skinMaterials = null, Model model = null)
        {
            this.scene = scene;
            guiContext = scene.GuiContext;
            this.mesh = mesh;
            VBIB = mesh.VBIB;
            if (model != null)
            {
                VBIB = model.RemapBoneIndices(VBIB, meshIndex);
            }
            mesh.GetBounds();
            BoundingBox = new AABB(mesh.MinBounds, mesh.MaxBounds);
            MeshIndex = meshIndex;

            ConfigureDrawCalls(skinMaterials, true);
        }

        public IEnumerable<string> GetSupportedRenderModes()
            => DrawCallsAll
                .SelectMany(drawCall => drawCall.Material.Shader.RenderModes)
                .Distinct();

        public void SetRenderMode(string renderMode)
        {
            var drawCalls = DrawCallsAll;

            foreach (var call in drawCalls)
            {
                // Recycle old shader parameters that are not render modes since we are scrapping those anyway
                var parameters = call.Material.Shader.Parameters
                    .Where(kvp => !kvp.Key.StartsWith("renderMode", StringComparison.InvariantCulture))
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

                if (renderMode != null && call.Material.Shader.RenderModes.Contains(renderMode))
                {
                    parameters.Add($"renderMode_{renderMode}", 1);
                }

                call.Material.Shader = guiContext.ShaderLoader.LoadShader(call.Material.Shader.Name, parameters);
                call.VertexArrayObject = guiContext.MeshBufferCache.GetVertexArrayObject(
                    VBIB,
                    call.Material,
                    call.VertexBuffer.Id,
                    call.IndexBuffer.Id,
                    call.BaseVertex);
            }
        }

        public void SetAnimationTexture(int? texture, int animationTextureSize)
        {
            AnimationTexture = texture;
            AnimationTextureSize = animationTextureSize;
        }

        public void SetSkin(Dictionary<string, string> skinMaterials)
        {
            ConfigureDrawCalls(skinMaterials, false);
        }

        private void ConfigureDrawCalls(Dictionary<string, string> skinMaterials, bool firstSetup)
        {
            var data = mesh.Data;
            var sceneObjects = data.GetArray("m_sceneObjects");

            if (firstSetup)
            {
                // This call has side effects because it uploads to gpu
                guiContext.MeshBufferCache.GetVertexIndexBuffers(VBIB);
            }

            foreach (var sceneObject in sceneObjects)
            {
                var i = 0;
                var objectDrawCalls = sceneObject.GetArray("m_drawCalls");
                var objectDrawBounds = sceneObject.ContainsKey("m_drawBounds")
                    ? sceneObject.GetArray("m_drawBounds")
                    : Array.Empty<IKeyValueCollection>();

                foreach (var objectDrawCall in objectDrawCalls)
                {
                    var materialName = objectDrawCall.GetProperty<string>("m_material") ?? objectDrawCall.GetProperty<string>("m_pMaterial");

                    if (skinMaterials != null && skinMaterials.ContainsKey(materialName))
                    {
                        materialName = skinMaterials[materialName];
                    }

                    var shaderArguments = new Dictionary<string, byte>(scene.RenderAttributes);

                    if (Mesh.IsCompressedNormalTangent(objectDrawCall))
                    {
                        var vertexBuffer = objectDrawCall.GetArray("m_vertexBuffers")[0]; // TODO: Not just 0
                        var vertexBufferId = vertexBuffer.GetInt32Property("m_hBuffer");
                        var inputLayout = VBIB.VertexBuffers[vertexBufferId].InputLayoutFields.FirstOrDefault(static i => i.SemanticName == "NORMAL");

                        var version = inputLayout.Format switch
                        {
                            DXGI_FORMAT.R32_UINT => (byte)2, // Added in CS2 on 2023-08-03
                            _ => (byte)1,
                        };

                        shaderArguments.Add("D_COMPRESSED_NORMALS_AND_TANGENTS", version);
                    }

                    if (Mesh.HasBakedLightingFromLightMap(objectDrawCall) && scene.LightingInfo.HasValidLightmaps)
                    {
                        shaderArguments.Add("D_BAKED_LIGHTING_FROM_LIGHTMAP", 1);
                    }
                    else if (Mesh.HasBakedLightingFromVertexStream(objectDrawCall))
                    {
                        shaderArguments.Add("D_BAKED_LIGHTING_FROM_VERTEX_STREAM", 1);
                    }

                    var material = guiContext.MaterialLoader.GetMaterial(materialName, shaderArguments);

                    if (firstSetup)
                    {
                        // TODO: Don't pass around so much shit
                        var drawCall = CreateDrawCall(objectDrawCall, material);
                        if (i < objectDrawBounds.Length)
                        {
                            drawCall.DrawBounds = new AABB(
                                objectDrawBounds[i].GetSubCollection("m_vMinBounds").ToVector3(),
                                objectDrawBounds[i].GetSubCollection("m_vMaxBounds").ToVector3()
                            );
                        }

                        DrawCallsAll.Add(drawCall);

                        if (drawCall.Material.IsBlended && !drawCall.Material.IsOverlay)
                        {
                            DrawCallsBlended.Add(drawCall);
                        }
                        else
                        {
                            DrawCallsOpaque.Add(drawCall);
                        }

                        i++;
                    }
                    else
                    {
                        var drawCall = DrawCallsAll[i++];
                        drawCall.Material = material;
                    }
                }
            }
        }

        private DrawCall CreateDrawCall(IKeyValueCollection objectDrawCall, RenderMaterial material)
        {
            var drawCall = new DrawCall();
            var primitiveType = objectDrawCall.GetProperty<object>("m_nPrimitiveType");

            if (primitiveType is byte primitiveTypeByte)
            {
                if ((RenderPrimitiveType)primitiveTypeByte == RenderPrimitiveType.RENDER_PRIM_TRIANGLES)
                {
                    drawCall.PrimitiveType = PrimitiveType.Triangles;
                }
            }
            else if (primitiveType is string primitiveTypeString)
            {
                if (primitiveTypeString == "RENDER_PRIM_TRIANGLES")
                {
                    drawCall.PrimitiveType = PrimitiveType.Triangles;
                }
            }

            if (drawCall.PrimitiveType != PrimitiveType.Triangles)
            {
                throw new NotImplementedException("Unknown PrimitiveType in drawCall! (" + primitiveType + ")");
            }

            drawCall.Material = material;

            var indexBufferObject = objectDrawCall.GetSubCollection("m_indexBuffer");

            var indexBuffer = default(DrawBuffer);
            indexBuffer.Id = indexBufferObject.GetUInt32Property("m_hBuffer");
            indexBuffer.Offset = indexBufferObject.GetUInt32Property("m_nBindOffsetBytes");
            drawCall.IndexBuffer = indexBuffer;

            var vertexElementSize = VBIB.VertexBuffers[(int)drawCall.VertexBuffer.Id].ElementSizeInBytes;
            drawCall.BaseVertex = objectDrawCall.GetUInt32Property("m_nBaseVertex") * vertexElementSize;
            //drawCall.VertexCount = objectDrawCall.GetUInt32Property("m_nVertexCount");

            var indexElementSize = VBIB.IndexBuffers[(int)drawCall.IndexBuffer.Id].ElementSizeInBytes;
            drawCall.StartIndex = objectDrawCall.GetUInt32Property("m_nStartIndex") * indexElementSize;
            drawCall.IndexCount = objectDrawCall.GetInt32Property("m_nIndexCount");

            if (objectDrawCall.ContainsKey("m_vTintColor"))
            {
                var tintColor = objectDrawCall.GetSubCollection("m_vTintColor").ToVector3();
                drawCall.TintColor = new OpenTK.Vector3(tintColor.X, tintColor.Y, tintColor.Z);
            }

            if (objectDrawCall.ContainsKey("m_nMeshID"))
            {
                drawCall.MeshId = objectDrawCall.GetInt32Property("m_nMeshID");
            }

            if (objectDrawCall.ContainsKey("m_nFirstMeshlet"))
            {
                drawCall.FirstMeshlet = objectDrawCall.GetInt32Property("m_nFirstMeshlet");
                drawCall.NumMeshlets = objectDrawCall.GetInt32Property("m_nNumMeshlets");
            }

            if (indexElementSize == 2)
            {
                //shopkeeper_vr
                drawCall.IndexType = DrawElementsType.UnsignedShort;
            }
            else if (indexElementSize == 4)
            {
                //glados
                drawCall.IndexType = DrawElementsType.UnsignedInt;
            }
            else
            {
                throw new UnexpectedMagicException("Unsupported index type", indexElementSize, nameof(indexElementSize));
            }

            var m_vertexBuffer = objectDrawCall.GetArray("m_vertexBuffers")[0]; // TODO: Not just 0

            var vertexBuffer = default(DrawBuffer);
            vertexBuffer.Id = m_vertexBuffer.GetUInt32Property("m_hBuffer");
            vertexBuffer.Offset = m_vertexBuffer.GetUInt32Property("m_nBindOffsetBytes");
            drawCall.VertexBuffer = vertexBuffer;

            drawCall.VertexArrayObject = guiContext.MeshBufferCache.GetVertexArrayObject(
                VBIB,
                drawCall.Material,
                drawCall.VertexBuffer.Id,
                drawCall.IndexBuffer.Id,
                drawCall.BaseVertex);

            return drawCall;
        }
    }

    internal interface IRenderableMeshCollection
    {
        List<RenderableMesh> RenderableMeshes { get; }
    }
}
