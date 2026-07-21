
using Reveal.Sdk.Dom.Visualizations;

namespace RevealExcel.Sdk
{
    public enum FileDataMode
    {
        NameOnly, // Option 1: Filename only without the file extension
        WithoutThumbnail, // Option 2: All FileData without ThumbnailInfo
        WithThumbnail // Option 3: All FileData with ThumbnailInfo
    }

    public class FileData
    {
        public string? DashboardFileName { get; set; }
        public string? DashboardTitle { get; set; }
        public string? DateCreated { get; set; }
        public string? DateModified { get; set; }
        public string? FakeOwner { get; set; }
        public string? FakeOwnerImageUrl { get; set; }
        public string? FakeDashboardImageUrl { get; set; }
        public IDictionary<string, object>? ThumbnailInfo { get; set; }
    }

    public class DashboardNames
    {
        public string? DashboardFileName { get; set; }
        public string? DashboardTitle { get; set; }
    }

    public class FileName
    {
        public string? DashboardFileName { get; set; }
        public string? DashboardTitle { get; set; }
    }

    public class VisualizationNames
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
        public string? Name { get; set; }
        public string? ChartType { get; set; }
        public string? ImageUrl { get; set; }
    }

    public class VisualizationInfo
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
        public string? Name { get; set; }
        public string? FullName { get; set; }
        public string? ChartType { get; set; }
        public string? ImageUrl { get; set; }
        public List<string>? Labels { get; set; }
        public List<string>? Values { get; set; }
        public List<string>? Rows { get; set; }
        public List<string>? Targets { get; set; }
    }


    //public class VisualizationChartInfo
    //{
    //    public string? DashboardFileName { get; set; }
    //    public string? DashboardTitle { get; set; }
    //    public string? VizId { get; set; }
    //    public string? VizTitle { get; set; }
    //    public string? VizChartType { get; set; }
    //    public string? VizImageUrl { get; set; }
    //    public string VizLabels { get; set; } = "";
    //    public string VizValues { get; set; } = "";
    //    public string VizRows { get; set; } = "";
    //    public string VizTargets { get; set; } = "";
    //}

    public class VisualizationChartInfo
    {
        public string? DashboardFileName { get; set; }
        public string? DashboardTitle { get; set; }
        public string? VizId { get; set; }
        public string? VizTitle { get; set; }
        public string? VizChartType { get; set; }
    }

    public class VisualizationWithType
    {
        public IVisualization? Visualization { get; set; }
        public Type? Type { get; set; }
    }

    public class FieldUpdateRequest
    {
        public string? FieldName { get; set; }
        public string? FieldLabel { get; set; }
    }

    public class FakeOwnerInfo
    {
        public string? Name { get; set; }
        public string? ImageUrl { get; set; }
    }
}