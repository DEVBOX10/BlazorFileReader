using System;

namespace Blazor.FileReader.Wasm.Demo.Shared
{
    public class SiteLog
    {
        public int ID { get; set; }
        public DateTime DateTime { get; set; }
        public Site Site { get; set; }
        public int SiteID { get; set; }
        public double[] Lev { get; set; }
    }
}
