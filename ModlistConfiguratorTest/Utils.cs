using System.Xml;

namespace ModlistConfiguratorTest;

public class Utils
{
    public static XmlNode GetSettings(string fixtureName)
    {
        var fixturePath = Path.GetFullPath($"../../../Fixtures/{fixtureName}.xml");
        return SettingsImporter.GetSettingsFromFile(fixturePath)
            ?.DocumentElement
            ?.ChildNodes[0] ?? throw new FileNotFoundException($"Fixture not found: {fixturePath}");
    }

    private Report DoSomething()
    {
        var report = new Report();
        report.Ok = false;
        report.Message = "Test";

        return report;
    }

    private void Test()
    {
        if (DoSomething() is var report and not { Ok: true })
        {
            throw new Exception($"Test failed. Reason: {report.Message}");
        }
    }
}

public class Report
{
    public Boolean Ok;
    public String Message = "Unknown Issue";
}
