using System.IO.Compression;

namespace MarkItDown.Tests;

internal static partial class TestDocumentFactory
{
    /// <summary>Creates a PPTX with text, a picture, a chart, and a grouped text shape.</summary>
    internal static MemoryStream CreateRichPptxStream()
    {
        var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(archive, "[Content_Types].xml",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Default Extension="xml" ContentType="application/xml"/>
                  <Default Extension="png" ContentType="image/png"/>
                  <Override PartName="/ppt/presentation.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml"/>
                  <Override PartName="/ppt/slides/slide1.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slide+xml"/>
                  <Override PartName="/ppt/charts/chart1.xml" ContentType="application/vnd.openxmlformats-officedocument.drawingml.chart+xml"/>
                </Types>
                """);
            AddEntry(archive, "_rels/.rels",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="ppt/presentation.xml"/>
                </Relationships>
                """);
            AddEntry(archive, "ppt/presentation.xml",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <p:presentation xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"
                                xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <p:sldIdLst><p:sldId id="256" r:id="rId1"/></p:sldIdLst>
                </p:presentation>
                """);
            AddEntry(archive, "ppt/_rels/presentation.xml.rels",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide" Target="slides/slide1.xml"/>
                </Relationships>
                """);
            AddEntry(archive, "ppt/media/image1.png", Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO8B9XcAAAAASUVORK5CYII="));
            AddEntry(archive, "ppt/charts/chart1.xml",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <c:chartSpace xmlns:c="http://schemas.openxmlformats.org/drawingml/2006/chart"
                              xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
                              xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <c:chart>
                    <c:title>
                      <c:tx>
                        <c:rich>
                          <a:bodyPr/>
                          <a:lstStyle/>
                          <a:p><a:r><a:t>Sales Chart</a:t></a:r></a:p>
                        </c:rich>
                      </c:tx>
                    </c:title>
                    <c:plotArea>
                      <c:barChart>
                        <c:barDir val="col"/>
                        <c:ser>
                          <c:idx val="0"/>
                          <c:order val="0"/>
                          <c:tx><c:strRef><c:strCache><c:ptCount val="1"/><c:pt idx="0"><c:v>Series 1</c:v></c:pt></c:strCache></c:strRef></c:tx>
                          <c:cat>
                            <c:strRef>
                              <c:strCache>
                                <c:ptCount val="2"/>
                                <c:pt idx="0"><c:v>Q1</c:v></c:pt>
                                <c:pt idx="1"><c:v>Q2</c:v></c:pt>
                              </c:strCache>
                            </c:strRef>
                          </c:cat>
                          <c:val>
                            <c:numRef>
                              <c:numCache>
                                <c:formatCode>General</c:formatCode>
                                <c:ptCount val="2"/>
                                <c:pt idx="0"><c:v>4</c:v></c:pt>
                                <c:pt idx="1"><c:v>7</c:v></c:pt>
                              </c:numCache>
                            </c:numRef>
                          </c:val>
                        </c:ser>
                      </c:barChart>
                    </c:plotArea>
                  </c:chart>
                </c:chartSpace>
                """);
            AddEntry(archive, "ppt/slides/slide1.xml",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"
                       xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
                       xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"
                       xmlns:c="http://schemas.openxmlformats.org/drawingml/2006/chart">
                  <p:cSld>
                    <p:spTree>
                      <p:nvGrpSpPr>
                        <p:cNvPr id="1" name=""/>
                        <p:cNvGrpSpPr/>
                        <p:nvPr/>
                      </p:nvGrpSpPr>
                      <p:grpSpPr>
                        <a:xfrm>
                          <a:off x="0" y="0"/>
                          <a:ext cx="0" cy="0"/>
                          <a:chOff x="0" y="0"/>
                          <a:chExt cx="0" cy="0"/>
                        </a:xfrm>
                      </p:grpSpPr>
                      <p:sp>
                        <p:nvSpPr>
                          <p:cNvPr id="2" name="Title 1"/>
                          <p:cNvSpPr><a:spLocks noGrp="1"/></p:cNvSpPr>
                          <p:nvPr><p:ph type="title"/></p:nvPr>
                        </p:nvSpPr>
                        <p:spPr/>
                        <p:txBody>
                          <a:bodyPr/>
                          <a:lstStyle/>
                          <a:p><a:r><a:t>Test Slide Title</a:t></a:r></a:p>
                        </p:txBody>
                      </p:sp>
                      <p:sp>
                        <p:nvSpPr>
                          <p:cNvPr id="3" name="Body 1"/>
                          <p:cNvSpPr/>
                          <p:nvPr/>
                        </p:nvSpPr>
                        <p:spPr/>
                        <p:txBody>
                          <a:bodyPr/>
                          <a:lstStyle/>
                          <a:p><a:r><a:t>Body text outside the group.</a:t></a:r></a:p>
                        </p:txBody>
                      </p:sp>
                      <p:pic>
                        <p:nvPicPr>
                          <p:cNvPr id="4" name="Picture 1" descr="Sample image"/>
                          <p:cNvPicPr/>
                          <p:nvPr/>
                        </p:nvPicPr>
                        <p:blipFill>
                          <a:blip r:embed="rId1"/>
                          <a:stretch><a:fillRect/></a:stretch>
                        </p:blipFill>
                        <p:spPr>
                          <a:xfrm>
                            <a:off x="0" y="0"/>
                            <a:ext cx="1000000" cy="1000000"/>
                          </a:xfrm>
                          <a:prstGeom prst="rect"><a:avLst/></a:prstGeom>
                        </p:spPr>
                      </p:pic>
                      <p:graphicFrame>
                        <p:nvGraphicFramePr>
                          <p:cNvPr id="5" name="Chart 1"/>
                          <p:cNvGraphicFramePr/>
                          <p:nvPr/>
                        </p:nvGraphicFramePr>
                        <p:xfrm>
                          <a:off x="0" y="0"/>
                          <a:ext cx="1000000" cy="1000000"/>
                        </p:xfrm>
                        <a:graphic>
                          <a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/chart">
                            <c:chart r:id="rId2"/>
                          </a:graphicData>
                        </a:graphic>
                      </p:graphicFrame>
                      <p:grpSp>
                        <p:nvGrpSpPr>
                          <p:cNvPr id="6" name="Group 1"/>
                          <p:cNvGrpSpPr/>
                          <p:nvPr/>
                        </p:nvGrpSpPr>
                        <p:grpSpPr>
                          <a:xfrm>
                            <a:off x="0" y="0"/>
                            <a:ext cx="0" cy="0"/>
                            <a:chOff x="0" y="0"/>
                            <a:chExt cx="0" cy="0"/>
                          </a:xfrm>
                        </p:grpSpPr>
                        <p:sp>
                          <p:nvSpPr>
                            <p:cNvPr id="7" name="Grouped Text"/>
                            <p:cNvSpPr/>
                            <p:nvPr/>
                          </p:nvSpPr>
                          <p:spPr/>
                          <p:txBody>
                            <a:bodyPr/>
                            <a:lstStyle/>
                            <a:p><a:r><a:t>Grouped text</a:t></a:r></a:p>
                          </p:txBody>
                        </p:sp>
                      </p:grpSp>
                    </p:spTree>
                  </p:cSld>
                </p:sld>
                """);
            AddEntry(archive, "ppt/slides/_rels/slide1.xml.rels",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="../media/image1.png"/>
                  <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/chart" Target="../charts/chart1.xml"/>
                </Relationships>
                """);
        }

        ms.Position = 0;
        return ms;
    }
}
