﻿using BepuPhysics.Collidables;
using BepuUtilities;
using BepuUtilities.Collections;
using BepuUtilities.Memory;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;

namespace BepuPhysics.CollisionDetection
{
    public struct MeshReduction : ICollisionTestContinuation
    {
        /// <summary>
        /// Flag used to mark a contact as being generated by the face of a triangle in its feature id.
        /// </summary>
        public const int FaceCollisionFlag = 32768;
        /// <summary>
        /// Minimum dot product between a triangle face and the contact normal for a collision to be considered a triangle face contact.
        /// </summary>
        public const float MinimumDotForFaceCollision = 0.999f;
        public Buffer<Triangle> Triangles;
        //MeshReduction relies on all of a mesh's triangles being in slot B, as they appear in the mesh collision tasks.
        //However, the original user may have provided this pair in unknown order and triggered a flip. We'll compensate for that when examining contact positions.
        public bool RequiresFlip;
        //The triangles array is in the mesh's local space. In order to test any contacts against them, we need to be able to transform contacts.
        public BepuUtilities.Quaternion MeshOrientation;
        public BoundingBox QueryBounds;
        //This uses all of the nonconvex reduction's logic, so we just nest it.
        public NonconvexReduction Inner;

        public void Create(int childManifoldCount, BufferPool pool)
        {
            Inner.Create(childManifoldCount, pool);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void OnChildCompleted<TCallbacks>(ref PairContinuation report, ConvexContactManifold* manifold, ref CollisionBatcher<TCallbacks> batcher)
            where TCallbacks : struct, ICollisionCallbacks
        {
            Inner.OnChildCompleted(ref report, manifold, ref batcher);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnChildCompletedEmpty<TCallbacks>(ref PairContinuation report, ref CollisionBatcher<TCallbacks> batcher) where TCallbacks : struct, ICollisionCallbacks
        {
            Inner.OnChildCompletedEmpty(ref report, ref batcher);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static unsafe void RemoveIfOutsideBounds(ref ConvexContactManifold manifold, ref int contactIndex, in Vector3 meshSpaceContact, in BoundingBox queryBounds)
        {
            //If a contact is outside of the mesh space bounding box that found the triangles to test, then two things are true:
            //1) The contact is almost certainly not productive; the bounding box included a frame of integrated motion and this contact was outside of it.
            //2) The contact may have been created with a triangle whose neighbor was not in the query bounds, and so the neighbor won't contribute any blocking.
            //The result is that such contacts have a tendency to cause ghost collisions. We'd rather not force the use of very small speculative margins,
            //so instead we explicitly kill off contacts which are outside the queried bounds.
            if (Vector3.Min(meshSpaceContact, queryBounds.Min) != queryBounds.Min ||
                Vector3.Max(meshSpaceContact, queryBounds.Max) != queryBounds.Max)
            {
                ConvexContactManifold.FastRemoveAt(ref manifold, contactIndex);
                --contactIndex;
            }

        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void ComputeMeshSpaceContacts(ref ConvexContactManifold manifold, in Matrix3x3 inverseMeshOrientation, bool requiresFlip, Vector3* meshSpaceContacts, out Vector3 meshSpaceNormal)
        {
            //First, if the manifold considers the mesh and its triangles to be shape B, then we need to flip it.
            if (requiresFlip)
            {
                //If the manifold considers the mesh and its triangles to be shape B, it needs to be flipped before being transformed.
                for (int i = 0; i < manifold.Count; ++i)
                {
                    Matrix3x3.Transform(Unsafe.Add(ref manifold.Contact0, i).Offset - manifold.OffsetB, inverseMeshOrientation, out meshSpaceContacts[i]);
                }
                Matrix3x3.Transform(-manifold.Normal, inverseMeshOrientation, out meshSpaceNormal);
            }
            else
            {
                //No flip required.
                for (int i = 0; i < manifold.Count; ++i)
                {
                    Matrix3x3.Transform(Unsafe.Add(ref manifold.Contact0, i).Offset, inverseMeshOrientation, out meshSpaceContacts[i]);
                }
                Matrix3x3.Transform(manifold.Normal, inverseMeshOrientation, out meshSpaceNormal);
            }
        }

        struct TestTriangle
        {
            //The test triangle contains AOS-ified layouts for quicker per contact testing.
            public Vector4 AnchorX;
            public Vector4 AnchorY;
            public Vector4 AnchorZ;
            public Vector4 NX;
            public Vector4 NY;
            public Vector4 NZ;
            public float DistanceThreshold;
            public int ChildIndex;
            /// <summary>
            /// True if the manifold associated with this triangle has been blocked due to its detected infringement on another triangle, false otherwise.
            /// </summary>
            public bool Blocked;
            /// <summary>
            /// True if the triangle did not act as a blocker for any other manifold and so can be removed if it is blocked, false otherwise.
            /// </summary>
            public bool AllowDeletion;
            /// <summary>
            /// Normal of a triangle detected as being infringed by the manifold associated with this triangle in mesh space.
            /// </summary>
            public Vector3 CorrectedNormal;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public TestTriangle(in Triangle triangle, int sourceChildIndex)
            {
                var ab = triangle.B - triangle.A;
                var bc = triangle.C - triangle.B;
                var ca = triangle.A - triangle.C;
                //TODO: This threshold might result in bumps when dealing with small triangles. May want to include a different source of scale information, like from the original convex test.
                DistanceThreshold = 1e-4f * (float)Math.Sqrt(MathHelper.Max(ab.LengthSquared(), bc.LengthSquared()));
                var n = Vector3.Cross(ab, ca);
                //Edge normals point outward.
                var edgeNormalAB = Vector3.Cross(n, ab);
                var edgeNormalBC = Vector3.Cross(n, bc);
                var edgeNormalCA = Vector3.Cross(n, ca);

                NX = new Vector4(n.X, edgeNormalAB.X, edgeNormalBC.X, edgeNormalCA.X);
                NY = new Vector4(n.Y, edgeNormalAB.Y, edgeNormalBC.Y, edgeNormalCA.Y);
                NZ = new Vector4(n.Z, edgeNormalAB.Z, edgeNormalBC.Z, edgeNormalCA.Z);
                var normalLengthSquared = NX * NX + NY * NY + NZ * NZ;
                var inverseLength = Vector4.One / Vector4.SquareRoot(normalLengthSquared);
                NX *= inverseLength;
                NY *= inverseLength;
                NZ *= inverseLength;
                AnchorX = new Vector4(triangle.A.X, triangle.A.X, triangle.B.X, triangle.C.X);
                AnchorY = new Vector4(triangle.A.Y, triangle.A.Y, triangle.B.Y, triangle.C.Y);
                AnchorZ = new Vector4(triangle.A.Z, triangle.A.Z, triangle.B.Z, triangle.C.Z);

                ChildIndex = sourceChildIndex;
                Blocked = false;
                AllowDeletion = true;
                CorrectedNormal = default;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe bool ShouldBlockNormal(in TestTriangle triangle, Vector3* meshSpaceContacts, int contactCount, in Vector3 meshSpaceNormal)
        {
            //While we don't have a decent way to do truly scaling SIMD operations within the context of a single manifold vs triangle test, we can at least use 4-wide operations
            //to accelerate each individual contact test. 
            for (int i = 0; i < contactCount; ++i)
            {
                // distanceFromPlane = (Position - a) * N / ||N||
                // distanceFromPlane^2 = ((Position - a) * N)^2 / (N * N)
                // distanceAlongEdgeNormal^2 = ((Position - edgeStart) * edgeN)^2 / ||edgeN||^2

                //There are four lanes, one for each plane of consideration:
                //X: Plane normal
                //Y: AB edge normal
                //Z: BC edge normal
                //W: CA edge normal
                //They're all the same operation, so we can do them 4-wide. That's better than doing a bunch of individual horizontal dot products.
                ref var contact = ref meshSpaceContacts[i];
                var px = new Vector4(contact.X);
                var py = new Vector4(contact.Y);
                var pz = new Vector4(contact.Z);
                var offsetX = px - triangle.AnchorX;
                var offsetY = py - triangle.AnchorY;
                var offsetZ = pz - triangle.AnchorZ;
                var distanceAlongNormal = offsetX * triangle.NX + offsetY * triangle.NY + offsetZ * triangle.NZ;
                //Note that very very thin triangles can result in questionable acceptance due to not checking for true distance- 
                //a position might be way outside a vertex, but still within edge plane thresholds. We're assuming that the impact of this problem will be minimal.
                if (distanceAlongNormal.X <= triangle.DistanceThreshold &&
                    distanceAlongNormal.Y <= triangle.DistanceThreshold &&
                    distanceAlongNormal.Z <= triangle.DistanceThreshold &&
                    distanceAlongNormal.W <= triangle.DistanceThreshold)
                {
                    //The contact is near the triangle. Is the normal infringing on the triangle's face region?
                    //This occurs when:
                    //1) The contact is near an edge, and the normal points inward along the edge normal.
                    //2) The contact is on the inside of the triangle.
                    var negativeThreshold = -triangle.DistanceThreshold;
                    var onAB = distanceAlongNormal.Y >= negativeThreshold;
                    var onBC = distanceAlongNormal.Z >= negativeThreshold;
                    var onCA = distanceAlongNormal.W >= negativeThreshold;
                    if (!onAB && !onBC && !onCA)
                    {
                        //The contact is within the triangle. 
                        //If this contact resulted in a correction, we can skip the remaining contacts in this manifold.
                        return true;
                    }
                    else
                    {
                        //The contact is on the border of the triangle. Is the normal pointing outward on any edge that the contact is on?
                        //Remember, the contact has been pushed into mesh space. The position is on the surface of the triangle, and the normal points from convex to mesh.
                        //The edge plane normals point outward from the triangle, so if the contact normal is detected as pointing along the edge plane normal,
                        //then it is infringing.
                        var normalDot = triangle.NX * meshSpaceNormal.X + triangle.NY * meshSpaceNormal.Y + triangle.NZ * meshSpaceNormal.Z;
                        const float infringementEpsilon = 5e-3f;
                        //In order to block a contact, it must be infringing on every edge that it is on top of.
                        //In other words, when a contact is on a vertex, it's not good enough to infringe only one of the edges; in that case, the contact normal isn't 
                        //actually infringing on the triangle face.
                        //Further, note that we require nonzero positive infringement; otherwise, we'd end up blocking the contacts of a flat neighbor.
                        //But we are a little more aggressive about blocking the *second* edge infringement- if it's merely parallel, we count it as infringing.
                        //Otherwise you could get into situations where a contact on the vertex of a bunch of different triangles isn't blocked by any of them because
                        //the normal is alinged with an edge.
                        if ((onAB && normalDot.Y > infringementEpsilon) || (onBC && normalDot.Z > infringementEpsilon) || (onCA && normalDot.W > infringementEpsilon))
                        {
                            //At least one edge is infringed. Are all contact-touched edges at least nearly infringed?
                            if ((!onAB || normalDot.Y > -infringementEpsilon) && (!onBC || normalDot.Z > -infringementEpsilon) && (!onCA || normalDot.W > -infringementEpsilon))
                            {
                                return true;
                            }
                        }
                    }
                }
            }
            return false;
        }


        public unsafe static void ReduceManifolds(ref Buffer<Triangle> continuationTriangles, ref Buffer<NonconvexReductionChild> continuationChildren, int start, int count,
            bool requiresFlip, in BoundingBox queryBounds, in Matrix3x3 meshOrientation, in Matrix3x3 meshInverseOrientation)
        {
            //Before handing responsibility off to the nonconvex reduction, make sure that no contacts create nasty 'bumps' at the border of triangles.
            //Bumps can occur when an isolated triangle test detects a contact pointing outward, like when a box hits the side. This is fine when the triangle truly is isolated,
            //but if there's a neighboring triangle that's snugly connected, the user probably wants the two triangles to behave as a single coherent surface. So, contacts
            //with normals which wouldn't exist in the ideal 'continuous' form of the surface need to be corrected.

            //A contact is a candidate for correction if it meets three conditions:
            //1) The contact was not generated by a face collision, and
            //2) The contact position is touching another triangle, and
            //3) The contact normal is infringing on the neighbor's face voronoi region.

            //Contacts generated by face collisions are always immediately accepted without modification. 
            //The only time they can cause infringement is when the surface is concave, and in that case, the face normal is correct and will not cause any inappropriate bumps.

            //A contact that isn't touching a triangle can't infringe upon it.
            //Note that triangle-involved manifolds always generate contacts such that the position is on the triangle to make this test meaningful.
            //(That's why the MeshReduction has to be aware of whether the manifolds have been flipped- so that we know we're working with consistent slots.)

            //Contacts generated by face collisions are marked with a special feature id flag. If it is present, we can skip the contact. The collision tester also provided unique feature ids
            //beyond that flag, so we can strip the flag now. (We effectively just hijacked the feature id to store some temporary metadata.)

            //TODO: Note that we perform contact correction prior to reduction. Reduction depends on normals to compute its 'constrainedness' heuristic.
            //You could sacrifice a little bit of reduction quality for faster contact correction (since reduction outputs a low fixed number of contacts), but
            //we should only pursue that if contact correction is a meaningful cost.

            //Narrow the region of interest.
            continuationTriangles.Slice(start, count, out var triangles);
            continuationChildren.Slice(start, count, out var children);
            //Allocate enough space for all potential triangles, even though we're only going to be enumerating over the subset which actually have contacts.
            int activeChildCount = 0;
            var activeTriangles = stackalloc TestTriangle[count];
            for (int i = 0; i < count; ++i)
            {
                if (children[i].Manifold.Count > 0)
                {
                    activeTriangles[activeChildCount] = new TestTriangle(triangles[i], i);
                    ++activeChildCount;
                }
            }
            var meshSpaceContacts = stackalloc Vector3[4];
            for (int i = 0; i < activeChildCount; ++i)
            {
                ref var sourceTriangle = ref activeTriangles[i];
                ref var sourceChild = ref children[sourceTriangle.ChildIndex];
                //Can't correct contacts that were created by face collisions.
                if ((sourceChild.Manifold.Contact0.FeatureId & FaceCollisionFlag) == 0)
                {
                    ComputeMeshSpaceContacts(ref sourceChild.Manifold, meshInverseOrientation, requiresFlip, meshSpaceContacts, out var meshSpaceNormal);
                    for (int j = 0; j < activeChildCount; ++j)
                    {
                        //No point in trying to check a normal against its own triangle.
                        if (i != j)
                        {
                            ref var targetTriangle = ref activeTriangles[j];
                            if (ShouldBlockNormal(targetTriangle, meshSpaceContacts, sourceChild.Manifold.Count, meshSpaceNormal))
                            {
                                sourceTriangle.Blocked = true;
                                sourceTriangle.CorrectedNormal = new Vector3(targetTriangle.NX.X, targetTriangle.NY.X, targetTriangle.NZ.X);
                                //Even if the target manifold gets blocked, it should not be deleted. We made use of it as a blocker.
                                targetTriangle.AllowDeletion = false;
                                break;
                            }
                        }
                    }
                    //Note that the removal had to be deferred until after blocking analysis.
                    //This manifold will not be considered for the remainder of this loop, so modifying it is fine.
                    for (int j = 0; j < sourceChild.Manifold.Count; ++j)
                    {
                        RemoveIfOutsideBounds(ref sourceChild.Manifold, ref j, meshSpaceContacts[j], queryBounds);
                    }
                }
                else
                {
                    //Clear the face flags. This isn't *required* since they're coherent enough anyway and the accumulated impulse redistributor is a decent fallback,
                    //but it costs basically nothing to do this.
                    for (int k = 0; k < sourceChild.Manifold.Count; ++k)
                    {
                        Unsafe.Add(ref sourceChild.Manifold.Contact0, k).FeatureId &= ~FaceCollisionFlag;
                    }
                }
            }
            for (int i = 0; i < activeChildCount; ++i)
            {
                ref var triangle = ref activeTriangles[i];
                if (triangle.Blocked)
                {
                    if (triangle.AllowDeletion)
                    {
                        //The manifold was infringing, and no other manifold infringed upon it. Can safely just ignore the manifold completely.
                        children[triangle.ChildIndex].Manifold.Count = 0;
                    }
                    else
                    {
                        //The manifold was infringing, but another manifold was infringing upon it. We can't safely delete such a manifold since it's likely a mutually infringing 
                        //case- consider what happens when an objects wedges itself into an edge between two triangles.                            
                        Matrix3x3.Transform(requiresFlip ? triangle.CorrectedNormal : -triangle.CorrectedNormal, meshOrientation, out children[triangle.ChildIndex].Manifold.Normal);

                        //Note that we do not modify the depth.
                        //The only time this situation should occur is when an object has somehow wedged between adjacent triangles such that the detected
                        //depths are *less* than the triangle face depths. So, using those depths is guaranteed not to introduce excessive energy.

                    }
                }
            }
        }

        //[MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe bool TryFlush<TCallbacks>(int pairId, ref CollisionBatcher<TCallbacks> batcher) where TCallbacks : struct, ICollisionCallbacks
        {
            Debug.Assert(Inner.ChildCount > 0);
            if (Inner.CompletedChildCount == Inner.ChildCount)
            {
                Matrix3x3.CreateFromQuaternion(MeshOrientation, out var meshOrientation);
                Matrix3x3.Transpose(meshOrientation, out var meshInverseOrientation);

                ReduceManifolds(ref Triangles, ref Inner.Children, 0, Inner.ChildCount, RequiresFlip, QueryBounds, meshOrientation, meshInverseOrientation);

                //Now that boundary smoothing analysis is done, we no longer need the triangle list.
                batcher.Pool.Return(ref Triangles);
                Inner.Flush(pairId, ref batcher);
                return true;
            }
            return false;
        }

    }
}
