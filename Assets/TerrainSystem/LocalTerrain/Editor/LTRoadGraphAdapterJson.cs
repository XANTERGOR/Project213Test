using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace LocalTerrainPrototype
{
    // Pure, lossless JSON transformation. Uses framework JSON/XML readers rather
    // than internal Shader Graph APIs or an optional third-party JSON package.
    internal static class LTRoadGraphAdapterJson
    {
        internal const int Version=1;
        static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
        internal static XElement Read(string json)
        {
            using(var reader=JsonReaderWriterFactory.CreateJsonReader(Encoding.UTF8.GetBytes(json),XmlDictionaryReaderQuotas.Max))
                return XElement.Load(reader);
        }
        internal static string Write(XElement value)
        {
            using(var stream=new MemoryStream())
            {
                using(var writer=JsonReaderWriterFactory.CreateJsonWriter(stream,Encoding.UTF8,false,true,"    "))new XElement(value){Name="root"}.WriteTo(writer);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
        internal static List<XElement> ReadGraph(string text)
        {
            var result=new List<XElement>();int start=-1,depth=0;bool quoted=false,escape=false;
            for(int i=0;i<text.Length;i++)
            {
                char c=text[i];
                if(start<0){if(char.IsWhiteSpace(c)||c=='\ufeff')continue;Require(c=='{',"Некорректный формат Shader Graph.");start=i;}
                if(quoted){if(escape)escape=false;else if(c=='\\')escape=true;else if(c=='"')quoted=false;continue;}
                if(c=='"'){quoted=true;continue;}
                if(c=='{')depth++;
                else if(c=='}'&&--depth==0){result.Add(Read(text.Substring(start,i-start+1)));start=-1;}
            }
            Require(start<0&&result.Count>0,"Незавершённый или пустой Shader Graph.");return result;
        }
        internal static string Text(XElement o,string key)=>o?.Element(key)?.Value;
        static XElement Ref(string id)=>Read("{\"m_Id\":\""+id+"\"}");
        static XElement Array(XElement o,string key)
        {var a=o.Element(key);Require(a!=null&&(string)a.Attribute("type")=="array","Отсутствует массив "+key);return a;}
        static void Set(XElement o,string key,XElement value)
        {var copy=new XElement(value){Name=key};var old=o.Element(key);if(old!=null)old.ReplaceWith(copy);else o.Add(copy);}
        static void Add(XElement array,XElement value){var copy=new XElement(value){Name="item"};array.Add(copy);}
        static string Id(XElement o)=>Text(o,"m_ObjectId");
        static string Reference(XElement o)=>Text(o,"m_Id");
        static XElement Edge(string from,int output,string to,int input)=>Read(
            "{\"m_OutputSlot\":{\"m_Node\":{\"m_Id\":\""+from+"\"},\"m_SlotId\":"+output+"},"+
            "\"m_InputSlot\":{\"m_Node\":{\"m_Id\":\""+to+"\"},\"m_SlotId\":"+input+"}}");
        static string Canonical(XElement e)
        {
            var clone=new XElement(e);
            void Sort(XElement n)
            {
                foreach(var c in n.Elements().ToArray())Sort(c);
                if((string)n.Attribute("type")=="object")n.ReplaceNodes(n.Elements().OrderBy(x=>x.Name.LocalName,StringComparer.Ordinal).ToArray());
                if((string)n.Attribute("type")=="number")n.Value=double.Parse(n.Value,CultureInfo.InvariantCulture).ToString("R",CultureInfo.InvariantCulture);
            }
            Sort(clone);return Write(clone);
        }
        internal static string Build(string source,string template)
        {
            var objects=ReadGraph(source);var g=objects[0];var t=Read(template);
            Require(Text(t,"version")==Version.ToString(),"Неизвестная версия шаблона адаптера.");
            Require(Text(g,"m_Type")=="UnityEditor.ShaderGraph.GraphData"&&Text(g,"m_SGVersion")==Text(t,"graphVersion"),"Эта версия Shader Graph ещё не поддерживается.");
            var byId=new Dictionary<string,XElement>(StringComparer.Ordinal);
            foreach(var o in objects){string id=Id(o);Require(!string.IsNullOrEmpty(id)&&!byId.ContainsKey(id),"Повторяющийся/пустой ObjectId.");byId.Add(id,o);}
            XElement Resolve(string id){Require(id!=null&&byId.ContainsKey(id),"Повреждённая ссылка Shader Graph: "+id);return byId[id];}
            var targets=Array(g,"m_ActiveTargets").Elements().ToArray();
            Require(targets.Length==1,"Нужен один HDRP Lit target.");var target=Resolve(Reference(targets[0]));
            Require(Text(target,"m_Type")=="UnityEditor.Rendering.HighDefinition.ShaderGraph.HDTarget"&&
                Text(Resolve(Reference(target.Element("m_ActiveSubTarget"))),"m_Type")=="UnityEditor.Rendering.HighDefinition.ShaderGraph.HDLitSubTarget","Поддерживается только HDRP Lit Shader Graph.");
            Require(Text(target,"m_SupportVFX")!="true"&&Text(target,"m_SupportLineRendering")!="true","VFX/Line Rendering не поддерживается адаптером дороги.");
            var data=Array(target,"m_Datas").Elements().Select(x=>Resolve(Reference(x))).ToArray();
            var contracts=Array(t,"contracts").Elements().ToArray();Require(data.Length==contracts.Length,"Неизвестные настройки HDRP target.");
            foreach(var contract in contracts)
            {
                var found=data.Where(x=>Text(x,"m_Type")==Text(contract,"m_Type")).ToArray();Require(found.Length==1,"Не найдены настройки "+Text(contract,"m_Type"));
                var actual=new XElement(found[0]);
                foreach(string field in new[]{"m_ObjectId","m_MaterialNeedsUpdateHash","inspectorFoldoutMask","m_DebugSymbols"})actual.Element(field)?.Remove();
                actual.Name=contract.Name;
                Require(Canonical(actual)==Canonical(contract),"Неподдерживаемые настройки "+Text(contract,"m_Type").Split('.').Last()+". Нужен прежний opaque HDRP Lit / Normal TS без displacement, tessellation и особой модели освещения; узлы, текстуры и свойства менять можно.");
            }
            foreach(var p in Array(g,"m_Properties").Elements().Concat(Array(g,"m_Keywords").Elements()))
            {
                var item=Resolve(Reference(p));string name=Text(item,"m_OverrideReferenceName");if(string.IsNullOrEmpty(name))name=Text(item,"m_DefaultReferenceName");
                Require(name==null||(!name.StartsWith("_LTRoad",StringComparison.Ordinal)&&!name.StartsWith("_LT_ROAD",StringComparison.Ordinal)&&name!="_LTSurfaceWorldToLocal"),"Имя свойства зарезервировано адаптером: "+name);
            }
            var edges=Array(g,"m_Edges");
            var vertexIds=Array(g.Element("m_VertexContext"),"m_Blocks").Elements().Select(Reference).ToArray();
            Require(!edges.Elements().Any(e=>vertexIds.Contains(Reference(e.Element("m_InputSlot")?.Element("m_Node")))),"Изменение вершин в графе не поддерживается: маска бленда должна совпадать с геометрией дороги.");
            foreach(var item in Array(t,"objects").Elements())
            {
                var o=new XElement(item){Name="root"};string id=Id(o);Require(!byId.ContainsKey(id),"Конфликт ObjectId адаптера. Не используйте производный граф как исходник.");
                objects.Add(o);byId.Add(id,o);
            }
            foreach(var pair in new[]{("m_Properties","properties"),("m_Keywords","keywords"),("m_Nodes","nodes")})
                foreach(var item in Array(t,pair.Item2).Elements())Add(Array(g,pair.Item1),item);
            foreach(var e in Array(t,"edges").Elements())Add(edges,e);
            var fn=Resolve(Text(t,"functionId"));var fragmentIds=Array(g.Element("m_FragmentContext"),"m_Blocks").Elements().Select(Reference).ToArray();
            string[] channels={"BaseColor","NormalTS","Smoothness","Occlusion","Metallic"};
            for(int i=0;i<channels.Length;i++)
            {
                string channel=channels[i];var matches=fragmentIds.Select(Resolve).Where(x=>Text(x,"m_Name")=="SurfaceDescription."+channel).ToArray();
                Require(matches.Length==1,"Добавьте блок "+channel+" в Fragment исходного графа.");var block=matches[0];
                var slots=Array(block,"m_Slots").Elements().ToArray();Require(slots.Length==1,"Неизвестный формат блока "+channel);var slot=Resolve(Reference(slots[0]));
                var input=Array(fn,"m_Slots").Elements().Select(x=>Resolve(Reference(x))).Single(x=>Text(x,"m_Id")==i.ToString());
                var value=slot.Element("m_Value");Require(value!=null,"Нет значения блока "+channel);
                if((i==0&&Text(slot,"m_Type")=="UnityEditor.ShaderGraph.ColorRGBMaterialSlot")||
                    (i==1&&Text(slot,"m_Type")=="UnityEditor.ShaderGraph.NormalMaterialSlot"))
                {
                    // Preserve Shader Graph's gamma/linear conversion for an
                    // unconnected colour, not just its three stored numbers.
                    foreach(string field in new[]{"m_Type","m_ColorMode","m_DefaultColor","m_Space"})
                        if(slot.Element(field)!=null)Set(input,field,slot.Element(field));
                }
                if(i==0&&value.Element("r")!=null)
                {
                    var color=Read("{\"x\":0,\"y\":0,\"z\":0}");Set(color,"x",value.Element("r"));Set(color,"y",value.Element("g"));Set(color,"z",value.Element("b"));value=color;
                }
                Set(input,"m_Value",value);Set(input,"m_DefaultValue",value);
                var incoming=edges.Elements().Where(e=>Reference(e.Element("m_InputSlot")?.Element("m_Node"))==Id(block)).ToArray();Require(incoming.Length<=1,"Несколько соединений у "+channel);
                if(incoming.Length==1)
                {
                    var end=incoming[0].Element("m_InputSlot");Require(Text(end,"m_SlotId")==Text(slot,"m_Id"),"Неизвестный вход блока "+channel);
                    Set(end,"m_Node",Ref(Id(fn)));Set(end,"m_SlotId",Read(i.ToString()));
                }
                Add(edges,Edge(Id(fn),9+i,Id(block),int.Parse(Text(slot,"m_Id"))));
            }
            Set(g,"m_Path",Read("\"Local Terrain\""));
            return string.Join("\n\n",objects.Select(Write))+"\n";
        }
    }
}
