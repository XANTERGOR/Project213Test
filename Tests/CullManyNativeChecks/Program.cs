using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

// Runs the production HLSL on the Windows D3D11 WARP software device. No Unity,
// scenes, user GPU device, packages, raster shaders or player performance testing.
unsafe class Program
{
    [DllImport("d3d11.dll",CallingConvention=CallingConvention.StdCall)]
    static extern int D3D11CreateDevice(nint adapter,int driver,nint software,uint flags,uint[] levels,uint count,uint sdk,out nint device,out uint level,out nint context);
    [DllImport("d3dcompiler_47.dll",CallingConvention=CallingConvention.StdCall)]
    static extern int D3DCompile(byte[] source,nuint length,string name,nint defines,nint include,string entry,string target,uint flags,uint flags2,out nint code,out nint errors);
    static nint Fn(nint obj,int slot)=>Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj),slot*IntPtr.Size);
    static void HR(int result,string operation){if(result<0)throw new COMException(operation,result);}
    [StructLayout(LayoutKind.Sequential)] struct BufferDesc{public uint bytes,usage,bind,cpu,misc,stride;}
    [StructLayout(LayoutKind.Sequential)] struct Subresource{public nint data;public uint pitch,slice;}
    [StructLayout(LayoutKind.Sequential)] struct Mapped{public nint data;public uint row,depth;}
    static readonly List<nint> owned=new();static nint device,context;
    static nint Own(nint item){owned.Add(item);return item;}
    static byte[] Blob(nint blob)
    {
        var data=((delegate* unmanaged[Stdcall]<nint,nint>)Fn(blob,3))(blob);
        var length=((delegate* unmanaged[Stdcall]<nint,nuint>)Fn(blob,4))(blob);
        var result=new byte[(int)length];Marshal.Copy(data,result,0,result.Length);return result;
    }
    static nint Shader(string source,string entry,string report)
    {
        var input=Encoding.UTF8.GetBytes(source);
        int status=D3DCompile(input,(nuint)input.Length,"LTDetailCull.compute",0,0,entry,"cs_5_0",2048,0,out var code,out var errors);
        string diagnostic=errors!=0?Encoding.UTF8.GetString(Blob(errors)):"";
        File.WriteAllText(Path.Combine(report,entry+"-compile.log"),diagnostic);
        if(errors!=0)Marshal.Release(errors);
        HR(status,entry+" compilation: "+diagnostic);
        try
        {
            var bytes=Blob(code);File.WriteAllBytes(Path.Combine(report,entry+".dxbc"),bytes);
            nint shader;fixed(byte* data=bytes)
                HR(((delegate* unmanaged[Stdcall]<nint,void*,nuint,nint,nint*,int>)Fn(device,18))(device,data,(nuint)bytes.Length,0,&shader),"CreateComputeShader");
            return Own(shader);
        }
        finally{Marshal.Release(code);}
    }
    static nint Buffer(uint bytes,uint bind,uint stride=0,bool staging=false,byte[] initial=null)
    {
        var desc=new BufferDesc{bytes=bytes,bind=bind,stride=stride,misc=stride==0?0u:64u,usage=staging?3u:0u,cpu=staging?0x20000u:0u};
        nint buffer;
        fixed(byte* data=initial)
        {
            var input=new Subresource{data=(nint)data};
            HR(((delegate* unmanaged[Stdcall]<nint,BufferDesc*,Subresource*,nint*,int>)Fn(device,3))(device,&desc,initial==null?null:&input,&buffer),"CreateBuffer");
        }
        return Own(buffer);
    }
    static nint View(nint buffer,uint count,bool uav)
    {
        // UNKNOWN format, BUFFER dimension, first element, element count, APPEND flag.
        uint* desc=stackalloc uint[6]{0,1,0,count,uav?2u:0u,0};nint view;
        HR(((delegate* unmanaged[Stdcall]<nint,nint,uint*,nint*,int>)Fn(device,uav?8:7))(device,buffer,desc,&view),"Create buffer view");return Own(view);
    }
    static void Update(nint buffer,byte[] data)
    {
        fixed(byte* p=data)((delegate* unmanaged[Stdcall]<nint,nint,uint,nint,void*,uint,uint,void>)Fn(context,48))(context,buffer,0,0,p,0,0);
    }
    static void Bind(int slot,nint value)=>((delegate* unmanaged[Stdcall]<nint,uint,uint,nint*,void>)Fn(context,slot))(context,0,1,&value);
    static void Outputs(nint[] views)
    {
        uint* counts=stackalloc uint[8];for(int i=0;i<8;i++)counts[i]=0;
        fixed(nint* ptr=views)((delegate* unmanaged[Stdcall]<nint,uint,uint,nint*,uint*,void>)Fn(context,68))(context,0,8,ptr,counts);
    }
    static void Dispatch(nint shader,int count)
    {
        ((delegate* unmanaged[Stdcall]<nint,nint,nint,uint,void>)Fn(context,69))(context,shader,0,0);
        ((delegate* unmanaged[Stdcall]<nint,uint,uint,uint,void>)Fn(context,41))(context,(uint)Math.Max(1,(count+63)/64),1,1);
    }
    static uint[] Read(nint source,nint staging,int count)
    {
        ((delegate* unmanaged[Stdcall]<nint,nint,nint,void>)Fn(context,47))(context,staging,source);
        Mapped mapped;HR(((delegate* unmanaged[Stdcall]<nint,nint,uint,uint,uint,Mapped*,int>)Fn(context,14))(context,staging,0,1,0,&mapped),"Map staging");
        try{var result=new uint[count];new ReadOnlySpan<uint>((void*)mapped.data,count).CopyTo(result);return result;}
        finally{((delegate* unmanaged[Stdcall]<nint,nint,uint,void>)Fn(context,15))(context,staging,0);}
    }
    static void Float(byte[] data,int offset,float value)=>BitConverter.GetBytes(value).CopyTo(data,offset);
    static void Int(byte[] data,int offset,int value)=>BitConverter.GetBytes(value).CopyTo(data,offset);
    static void Vector(byte[] data,int offset,float x,float y,float z,float w)
    {Float(data,offset,x);Float(data,offset+4,y);Float(data,offset+8,z);Float(data,offset+12,w);}
    static int Main()
    {
        const string report="Logs/GrassCullMany/Native";Directory.CreateDirectory(report);
        try
        {
            HR(D3D11CreateDevice(0,5,0,0,new uint[]{0xb000},1,7,out device,out uint level,out context),"Create WARP device");
            Console.WriteLine($"D3D11 WARP feature level {level:X}; native compute correctness only.");
            string source=File.ReadAllText("Assets/TerrainSystem/LocalTerrain/Resources/LTDetailCull.compute");
            string originalReport=Path.Combine(report,"Original");Directory.CreateDirectory(originalReport);
            Shader(source,"Cull",originalReport);Shader(source,"CullMany",originalReport);
            // Explicit test ABI. Kernel bodies are unmodified; Unity normally reflects these
            // uniforms/resources by name. cb layout matches HLSL's 16-byte packing rules.
            string ExplicitBindings(string source)
            {
            source=Regex.Replace(source,@"#pragma[^\r\n]*","");
            source=source.Replace("float4 _Selections[8];","" );
            source=source.Replace("float4 _Planes[6];","cbuffer Params : register(b0) { float4 _Planes[6];");
            source=source.Replace("int _PrefabShadowMode, _DrawShadowMode, _LimitShadows;","int _PrefabShadowMode, _DrawShadowMode, _LimitShadows; float4 _Selections[8]; };" );
            source=source.Replace("AppendStructuredBuffer<uint> _LTVisible;","AppendStructuredBuffer<uint> _LTVisible : register(u0);" );
            source=Regex.Replace(source,@"AppendStructuredBuffer<uint> (_LTVisible[0-7][^;]*);",m=>
                string.Join("\n",m.Groups[1].Value.Split(',').Select(n=>{n=n.Trim();return "AppendStructuredBuffer<uint> "+n+" : register(u"+n[^1]+");";})));
                return source;
            }
            var oldShader=Shader(ExplicitBindings(File.ReadAllText("Tests/CullManyNativeChecks/LegacyCull.compute")),"Cull",report);
            var newShader=Shader(ExplicitBindings(source),"CullMany",report);
            const int capacity=257;var roots=new byte[capacity*192];var random=new Random(213);
            for(int i=0;i<capacity;i++)
            {
                float x=(float)(random.NextDouble()*220-110),y=(float)(random.NextDouble()*50-25),z=(float)(random.NextDouble()*180-30);
                if(i<6){x=y=0;z=new[]{0f,35,100,99.999f,100.001f,-.001f}[i];}
                int p=i*192;Vector(roots,p+128,x,y,z,.1f+(float)random.NextDouble()*12);
                Vector(roots,p+144,x+.2f,y+.3f,z,(float)random.NextDouble());
                Vector(roots,p+160,x,y,z,0);Vector(roots,p+176,.3f,1,.3f,0);
            }
            var root=Buffer((uint)roots.Length,8,192,initial:roots);var srv=View(root,capacity,false);Bind(67,srv);
            var constants=Buffer(448,4);Bind(71,constants);
            var outputs=new nint[8];var views=new nint[8];
            for(int i=0;i<8;i++){outputs[i]=Buffer(capacity*4,128,4);views[i]=View(outputs[i],capacity,true);}
            var counter=Buffer(4,0);var counterRead=Buffer(4,0,staging:true);var outputRead=Buffer(capacity*4,0,staging:true);
            uint[] ReadList(int slot)
            {
                ((delegate* unmanaged[Stdcall]<nint,nint,uint,nint,void>)Fn(context,49))(context,counter,0,views[slot]);
                uint count=Read(counter,counterRead,1)[0];if(count>capacity)throw new Exception("Append overflow");
                var result=Read(outputs[slot],outputRead,(int)count);Array.Sort(result);return result;
            }
            int comparisons=0,scenarios=0;
            for(int lods=1;lods<=8;lods++)for(int variation=0;variation<8;variation++)
            {
                var data=new byte[448];int count=new[]{0,1,63,64,65,129,256,257}[variation];
                Vector(data,0,1,0,0,80);Vector(data,16,-1,0,0,80);Vector(data,32,0,1,0,20);
                Vector(data,48,0,-1,0,20);Vector(data,64,0,0,1,30);Vector(data,80,0,0,-1,140);
                Vector(data,96,0,0,0,0);Vector(data,112,0,0,1,0);Vector(data,128,100,50,.31f,35);
                Vector(data,144,variation%2,variation%2==0?.5773503f:35,variation<4?1:1.7f,variation==7?lods-1:0);
                for(int i=0;i<8;i++)Vector(data,160+i*16,.6f/(1<<i),0,0,0);
                Int(data,288,count);Int(data,292,lods);Int(data,296,lods>1?1:0);Int(data,304,variation/2%2);Int(data,316,variation/4);
                var descriptors=new List<(int lod,int prefab,int mode)>();
                for(int lod=0;lod<lods;lod++)foreach(int prefab in new[]{0,1,2,3})
                {descriptors.Add((lod,prefab,prefab));if(prefab==1||prefab==2)descriptors.Add((lod,prefab,0));}
                for(int first=0;first<descriptors.Count;first+=8)
                {
                    var reference=new uint[8][];int length=Math.Min(8,descriptors.Count-first);
                    for(int slot=0;slot<8;slot++)
                    {
                        bool enabled=slot<length&&!(variation==6&&slot%3==0);
                        if(!enabled){Vector(data,320+slot*16,0,0,0,0);reference[slot]=Array.Empty<uint>();continue;}
                        var d=descriptors[first+slot];Vector(data,320+slot*16,d.lod,d.prefab,d.mode,1);
                        Int(data,300,d.lod);Int(data,308,d.prefab);Int(data,312,d.mode);
                        Update(constants,data);Outputs(views);Dispatch(oldShader,count);reference[slot]=ReadList(0);
                    }
                    Update(constants,data);Outputs(views);Dispatch(newShader,count);
                    for(int slot=0;slot<8;slot++)
                    {
                        var result=ReadList(slot);
                        if(!result.SequenceEqual(reference[slot]))throw new Exception($"Mismatch LODs={lods}, variation={variation}, first={first}, slot={slot}: old={string.Join(',',reference[slot])}; new={string.Join(',',result)}");
                        comparisons++;
                    }
                }
                scenarios++;
            }
            Console.WriteLine($"PASS production HLSL: {scenarios} scenarios, {comparisons} sorted append-list comparisons; 1..8 LODs, perspective/ortho, maxLOD, thinning, shadow modes/ranges, partial thread groups, inactive slots and boundary distances.");
            Console.WriteLine("WARP compute only. Unity native import, D3D12, raster/image parity and live-scene FPS remain unmeasured.");return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
        finally
        {
            for(int i=owned.Count-1;i>=0;i--)Marshal.Release(owned[i]);
            if(context!=0)Marshal.Release(context);if(device!=0)Marshal.Release(device);
        }
    }
}
