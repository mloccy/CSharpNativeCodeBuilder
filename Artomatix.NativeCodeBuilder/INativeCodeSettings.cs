using System.Xml.Serialization;

namespace Artomatix.NativeCodeBuilder
{
    public interface INativeCodeSettings
    {
        string PathToNativeCodeBase { get; }
        string[] DLLTargets { get; }
        string CMakeGenerationArguments { get; }
        public string CMakeBuildArguments { get; }
        public string CMakeGenerator { get; }
        string BuildStampPath { get; }
        string BuildPathBase { get; }
        public string[] NativeFileExtensions { get; set; }

    }

    public class NativeCodeSettings : INativeCodeSettings
    {
        public string PathToNativeCodeBase { get; set; }

        [XmlArray]
        [XmlArrayItem("Target")]
        public string[] DLLTargets { get; set; }

        public string CMakeGenerationArguments { get; set; }
        public string CMakeBuildArguments { get; set; }
        public string CMakeGenerator { get; set; }
        public string BuildStampPath { get; set; }

        [XmlArray]
        [XmlArrayItem("Extension")]
        public string[] NativeFileExtensions { get; set; }

        public string BuildPathBase { get; set; }


    }

    public interface INativeSettingsSerializer
    {
        string Serialize(NativeCodeSettings settings);

        INativeCodeSettings Deserialize(string serialized);
    }
}