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
            this.Sha1 = string.Empty;
            this.Crc32 = string.Empty;
            this.Mtime = string.Empty;
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

        /// <summary>Gets or sets a value indicating whether this is a replaced copy archive.org keeps under history/.</summary>
        [XmlElement("old_version")]
        public bool OldVersion { get; set; }

        [XmlElement("md5")]
        public string Md5 { get; set; }

        [XmlElement("sha1")]
        public string Sha1 { get; set; }

        [XmlElement("crc32")]
        public string Crc32 { get; set; }

        /// <summary>Gets or sets the upload time on archive.org, in Unix seconds as text (empty when not listed).</summary>
        [XmlElement("mtime")]
        public string Mtime { get; set; }

        /// <summary>Gets or sets, for a derivative, the file it was made from.</summary>
        [XmlElement("original")]
        public string? Original { get; set; }
    }
}