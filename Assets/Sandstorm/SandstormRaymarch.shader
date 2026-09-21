Shader "Sandstorm/HDRP Raymarch"
{
    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" }
        HLSLINCLUDE
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/RenderPass/CustomPass/CustomPassCommon.hlsl"
#include "SandstormDensity.hlsl"

            TEXTURE2D(_SandstormLowResColor);
            float4 _StormFullSize, _StormLowSize;
            float _StormHalfPass, _StormDepthTolerance, _StormRefineEdges;

            // The same integer depth coordinate is used in low-resolution rendering
            // and reconstruction. Snapping avoids pairing a ray with a neighbour's depth.
            float2 StormSourcePixel(int2 lowPixel)
            {
                return min(floor((float2(lowPixel) + 0.5) * _StormFullSize.xy / _StormLowSize.xy),
                    _StormFullSize.xy - 1) + 0.5;
            }

            float4x4 _VolumeWorldToLocal;
            float4 _MarchParams;
            float4 _AdaptiveMarch; // enabled, max multiplier, opacity start, opacity end
            float4 _SunDirection, _SunRadiance, _DustColor, _AmbientRadiance;
            float _Anisotropy;
            TEXTURE3D(_SunLightCache);
            SAMPLER(sampler_SunLightCache);
            float _UseLightCache;
            TEXTURE3D(_StormOccupancy);
            float _StormSkipEmpty, _StormOccupancySize;
            float EmptyDistance(float3 p, float3 dir)
            {
                float3 uv=p+0.5, d=dir;
                if (_AxisOptions.x>0.5) { uv=uv.xzy; d=d.xzy; }
                uv=lerp(uv,1-uv,_AxisOptions.yzw);
                d*=1-2*_AxisOptions.yzw;
                int size=(int)_StormOccupancySize;
                int3 cell=clamp((int3)floor(uv*size),0,size-1);
                if (_StormOccupancy.Load(int4(cell,0)).r>0) return 0;
                float result=1e20;
                [unroll] for(int axis=0;axis<3;axis++)
                {
                    if(abs(d[axis])>1e-8)
                    {
                        float face=(cell[axis]+(d[axis]>0 ? 1:0))/(float)size;
                        result=min(result,max(0,(face-uv[axis])/d[axis]));
                    }
                }
                return result;
            }

            bool IntersectBox(float3 o, float3 d, out float enter, out float leave)
            {
                enter = -1e20;
                leave = 1e20;
                [unroll] for (int axis = 0; axis < 3; axis++)
                {
                    if (abs(d[axis]) < 1e-8)
                    {
                        if (o[axis] < -0.5 || o[axis] > 0.5) return false;
                    }
                    else
                    {
                        float a = (-0.5 - o[axis]) / d[axis];
                        float b = ( 0.5 - o[axis]) / d[axis];
                        enter = max(enter, min(a, b));
                        leave = min(leave, max(a, b));
                    }
                }
                return leave > max(enter, 0.0);
            }



            float DirectSunTransmission(float3 p, float3 lightLocal)
            {
                float enter, leave;
                if (!IntersectBox(p, lightLocal, enter, leave)) return 1;
                int count = (int)_MarchParams.y;
                float ds = leave / count;
                float opticalDepth = 0;
                [loop] for (int j = 0; j < count; j++)
                {
                    opticalDepth += Density(p + lightLocal * ((j + 0.5) * ds)) * _DensityParams.z * ds;
                    if (opticalDepth > 10) break;
                }
                return exp(-opticalDepth);
            }

            float SunTransmission(float3 p, float3 lightLocal)
            {
                [branch] if (_UseLightCache > 0.5)
                    return saturate(SAMPLE_TEXTURE3D_LOD(_SunLightCache, sampler_SunLightCache, saturate(p + 0.5), 0).r);
                return DirectSunTransmission(p, lightLocal);
            }

            float4 RaymarchAtPixel(float2 fullPixel)
            {
                float rawDepth = LoadCameraDepth(fullPixel);
                PositionInputs posInput = GetPositionInput(fullPixel, _ScreenSize.zw, rawDepth, UNITY_MATRIX_I_VP, UNITY_MATRIX_V);
                float3 rayDir = -GetWorldSpaceNormalizeViewDir(posInput.positionWS);
                float3 cameraAbs = GetAbsolutePositionWS(GetCurrentViewPosition());
                float3 origin = mul(_VolumeWorldToLocal, float4(cameraAbs, 1)).xyz;
                // Never normalize transformed directions: ray distance stays in world metres.
                float3 direction = mul((float3x3)_VolumeWorldToLocal, rayDir);
                float enter, leave;
                if (!IntersectBox(origin, direction, enter, leave)) return 0;
                enter = max(enter, 0);
                if (rawDepth != UNITY_RAW_FAR_CLIP_VALUE)
                {
                    float3 sceneAbs = GetAbsolutePositionWS(posInput.positionWS);
                    leave = min(leave, max(0, dot(sceneAbs - cameraAbs, rayDir)));
                }
                if (leave <= enter) return 0;
                int count = (int)_MarchParams.x;
                float ds = (leave - enter) / count;
                float noise = frac(52.9829189 * frac(dot(fullPixel, float2(0.06711056, 0.00583715))));
                float offset = lerp(0.5, noise, _MarchParams.z);
                float3 lightDir = normalize(_SunDirection.xyz);
                float3 lightLocal = mul((float3x3)_VolumeWorldToLocal, lightDir);
                float g = _Anisotropy;
                float mu = dot(rayDir, lightDir);
                // HG angular shape, scaled so isotropic scattering has a multiplier of one.
                float phase = (1 - g * g) / pow(max(1 + g * g - 2 * g * mu, 0.001), 1.5);
                float transmittance = 1;
                float3 radiance = 0;
                // Cursor in base-step units. Each segment covers its full length;
                // clipping the last segment also respects opaque scene geometry.
                float cursor = 0;
                [loop] for (int i = 0; i < count; i++)
                {
                    float segmentUnits = 1;
                    [branch] if (_AdaptiveMarch.x > 0.5)
                    {
                        if (cursor >= count) break;
                        float opacity = 1 - transmittance;
                        float ramp = smoothstep(_AdaptiveMarch.z, _AdaptiveMarch.w, opacity);
                        segmentUnits = min(lerp(1.0, _AdaptiveMarch.y, ramp), count - cursor);
                    }
                    else if (_StormSkipEmpty < 0.5) cursor = (float)i; // Preserve original fixed-step sample positions.
                    float segmentLength = segmentUnits * ds;
                    float3 p = origin + direction * (enter + (cursor + offset * segmentUnits) * ds);
                    if (cursor >= count) break;
                    [branch] if (_StormSkipEmpty > 0.5)
                    {
                        float skipped=floor(EmptyDistance(p,direction)/max(ds,1e-20));
                        if(skipped>=1) { cursor+=min(skipped,count-cursor); continue; }
                        // A skipped prefix may leave a fractional final segment.
                        segmentUnits=min(segmentUnits,count-cursor);
                        segmentLength=segmentUnits*ds;
                        p=origin+direction*(enter+(cursor+offset*segmentUnits)*ds);
                    }
                    float density = Density(p);
                    if (density > 0.00001)
                    {
                        float alpha = 1 - exp(-density * _DensityParams.z * segmentLength);
                        float shadow = SunTransmission(p, lightLocal);
                        float3 lighting = _AmbientRadiance.rgb + _SunRadiance.rgb * shadow * phase;
                        radiance += transmittance * alpha * _DustColor.rgb * lighting;
                        transmittance *= 1 - alpha;
                        if (transmittance < _MarchParams.w) break;
                    }
                    cursor += segmentUnits;
                }
                // HDRP camera colour is pre-exposed at BeforeTransparent.
                return float4(radiance * GetCurrentExposureMultiplier(), 1 - transmittance);
            }

            float4 FullScreenPass(Varyings varyings) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(varyings);
                float2 pixel = varyings.positionCS.xy;
                if (_StormHalfPass > 0.5) pixel = StormSourcePixel(int2(pixel));
                return RaymarchAtPixel(pixel);
            }

            float DepthMismatch(float a, float b)
            {
                bool skyA = a == UNITY_RAW_FAR_CLIP_VALUE;
                bool skyB = b == UNITY_RAW_FAR_CLIP_VALUE;
                if (skyA && skyB) return 0;
                if (skyA != skyB) return 1e6;
                float za = LinearEyeDepth(a, _ZBufferParams);
                float zb = LinearEyeDepth(b, _ZBufferParams);
                return abs(za - zb) / max(min(za, zb), 1.0);
            }

            float4 CompositePass(Varyings varyings) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(varyings);
                float2 fullPixel = varyings.positionCS.xy;
                float targetDepth = LoadCameraDepth(uint2(fullPixel));
                float2 coord = fullPixel * _StormLowSize.xy / _StormFullSize.xy - 0.5;
                int2 basePixel = int2(floor(coord));
                float2 f = frac(coord);
                float4 sum = 0;
                float weightSum = 0;
                float bestError = 1e20;
                float4 closestColor = 0;
                [unroll] for (int y = 0; y < 2; y++)
                {
                    [unroll] for (int x = 0; x < 2; x++)
                    {
                        int2 q = clamp(basePixel + int2(x,y), int2(0,0), int2(_StormLowSize.xy)-1);
                        float4 color = LOAD_TEXTURE2D(_SandstormLowResColor, q);
                        float d = LoadCameraDepth(uint2(StormSourcePixel(q)));
                        float error = DepthMismatch(targetDepth, d);
                        if (error < bestError) { bestError = error; closestColor = color; }
                        float spatial = (x == 0 ? 1-f.x : f.x) * (y == 0 ? 1-f.y : f.y);
                        // Reject depth discontinuities instead of blurring across them.
                        float agreement = saturate(1 - error / max(_StormDepthTolerance, 0.0001));
                        float w = spatial * agreement * agreement;
                        sum += color * w;
                        weightSum += w;
                    }
                }
                if (weightSum > 1e-5) return sum / weightSum;
                // Thin geometry can be absent from all four low-res samples.
                // Re-evaluate only these pixels at full resolution.
                if (_StormRefineEdges > 0.5) return RaymarchAtPixel(fullPixel);
                return closestColor;
            }

        ENDHLSL
        Pass
        {
            Name "Sandstorm"
            ZWrite Off
            ZTest Always
            Cull Off
            Blend One OneMinusSrcAlpha
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment FullScreenPass
            #pragma multi_compile_instancing
            ENDHLSL
        }
        Pass
        {
            Name "DepthAwareUpsample"
            ZWrite Off
            ZTest Always
            Cull Off
            Blend One OneMinusSrcAlpha
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment CompositePass
            #pragma multi_compile_instancing
            ENDHLSL
        }
    }
    Fallback Off
}
