namespace ArchiveOrgCollectionSync
{
    using System.Xml.Serialization;

    public class File
    {
        public File()
        {
            this.Name = string.Empty;
            this.Md5 = string.Empty;
            this.Source = string.Empty;
        }

        [XmlAttribute("name")]
        public string Name { get; set; }

        [XmlAttribute("source")]
        public string Source { get; set; }

        [XmlElement("size")]
        public long Size { get; set; }

        /// <summary>Gets or sets a value indicating whether the file list had a size element (XmlSerializer "Specified" pattern).</summary>
        [XmlIgnore]
        public bool SizeSpecified { get; set; }

        [XmlElement("md5")]
        public string Md5 { get; set; }
    }
}