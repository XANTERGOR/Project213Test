using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using LocalTerrainPrototype;

class Program
{
    static int checks;
    static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
    static void Main(string[] args)
    {
        const string root="Assets/TerrainSystem/LocalTerrain/";
        string source=File.ReadAllText("Assets/Data/Models/Environment/Roads/Roads_1/Road_Shader.shadergraph");
        string template=File.ReadAllText(root+"Editor/LTRoadGraphBlendTemplate.json");
        string Build(string input)=>LTRoadGraphAdapterJson.Build(input,template);
        var result=Build(source);
        if(args.Contains("--emit")){Console.Write(result);return;}
        string Text(XElement e,string key)=>LTRoadGraphAdapterJson.Text(e,key);
        var original=LTRoadGraphAdapterJson.ReadGraph(source);var generated=LTRoadGraphAdapterJson.ReadGraph(result);
        Check(Build(source)==result,"Not deterministic");
        Check(generated.Select(o=>Text(o,"m_ObjectId")).Distinct().Count()==generated.Count,"Duplicate IDs");
        foreach(var o in original.Skip(1))Check(XNode.DeepEquals(o,generated.Single(x=>Text(x,"m_ObjectId")==Text(o,"m_ObjectId"))),"Authored object changed: "+Text(o,"m_ObjectId"));
        foreach(var e in original[0].Elements().Where(e=>!new[]{"m_Properties","m_Keywords","m_Nodes","m_Edges","m_Path"}.Contains(e.Name.LocalName)))
            Check(XNode.DeepEquals(e,generated[0].Element(e.Name)),"Unrelated root field changed: "+e.Name);
        void Reject(string input,string label)
        {bool failed=false;try{Build(input);}catch(InvalidOperationException){failed=true;}Check(failed,label);}
        Reject(source.Replace("\"m_SurfaceType\": 0","\"m_SurfaceType\": 1"),"Accepted transparent graph");
        Reject(source.Replace("\"m_Tessellation\": false","\"m_Tessellation\": true"),"Accepted tessellation");
        Reject(source.Replace("\"m_NormalDropOffSpace\": 0","\"m_NormalDropOffSpace\": 1"),"Accepted world normals");
        Reject(source.Replace("\"m_AlphaTest\": false","\"m_AlphaTest\": true"),"Accepted alpha clipping");
        Reject(source.Replace("\"m_RayTracing\": false","\"m_RayTracing\": true"),"Accepted unsupported ray tracing");
        Reject(result,"Accepted adapter as source");
        Reject(source+"{","Accepted incomplete graph");
        Reject(source+LTRoadGraphAdapterJson.Write(original[1]),"Accepted duplicate IDs");
        var modified=LTRoadGraphAdapterJson.ReadGraph(source);
        var rootNode=modified[0];var color=modified.Single(o=>Text(o,"m_Name")=="SurfaceDescription.BaseColor");
        rootNode.Element("m_Edges").Elements().Where(e=>Text(e.Element("m_InputSlot")?.Element("m_Node"),"m_Id")==Text(color,"m_ObjectId")).Remove();
        string changed=string.Join("\n\n",modified.Select(LTRoadGraphAdapterJson.Write));
        var flat=LTRoadGraphAdapterJson.ReadGraph(Build(changed));
        var input=flat.Single(o=>Text(o,"m_DisplayName")=="BaseColor"&&Text(o,"m_SlotType")=="0"&&Text(o,"m_Type")=="UnityEditor.ShaderGraph.ColorRGBMaterialSlot");
        var defaultColor=modified.Single(o=>Text(o,"m_ObjectId")==Text(color.Element("m_Slots").Elements().Single(),"m_Id")).Element("m_Value");
        Check(Text(input.Element("m_Value"),"x")== (Text(defaultColor,"r")??Text(defaultColor,"x")),"Disconnected color default lost");
        var vertex=rootNode.Element("m_VertexContext").Element("m_Blocks").Elements().First();
        var edge=new XElement(rootNode.Element("m_Edges").Elements().First());
        edge.Element("m_InputSlot").Element("m_Node").Element("m_Id").Value=Text(vertex,"m_Id");rootNode.Element("m_Edges").Add(edge);
        Reject(string.Join("\n\n",modified.Select(LTRoadGraphAdapterJson.Write)),"Accepted vertex displacement");
        // Parser must not split JSON objects on braces/quotes inside names/code.
        string strange="{\"name\":\"brace } { quote \\\" slash \\\\ Юникод\",\"nested\":{\"a\":[1,2]}}";
        var two=LTRoadGraphAdapterJson.ReadGraph(strange+"\n\n"+strange);Check(two.Count==2,"Escaped JSON split");
        Check(XNode.DeepEquals(two[0],LTRoadGraphAdapterJson.Read(LTRoadGraphAdapterJson.Write(two[0]))),"JSON roundtrip");
        Console.WriteLine($"PASS {checks} graph adapter checks: current source, unchanged authored objects/root data, deterministic generation, disconnected defaults, JSON escapes and incompatible-graph rejection. No Unity import/GPU claim.");
    }
}
