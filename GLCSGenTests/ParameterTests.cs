using System.Xml.Linq;
using GLCSGen.Spec;
using NUnit.Framework;

namespace GLCSGenTests
{
    [TestFixture]
    public class ParameterTests
    {
        [Test]
        public void CanParseSimpleParameterDeclaration()
        {
            var node = XElement.Parse(@"<param group=""AccumOp""><ptype>GLenum</ptype> <name>op</name></param>");
            var param = GlParameter.Parse(node);
            Assert.That(param.Group, Is.EqualTo("AccumOp"));
            Assert.That(param.Type.BaseType, Is.EqualTo(GlBaseType.Enum));
            Assert.That(param.Type.Modifier, Is.EqualTo(GlTypeModifier.None));
            Assert.That(param.Name, Is.EqualTo("op"));
        }

        [Test]
        public void CanParseParameterDeclarationWithoutPtypeNode()
        {
            var node = XElement.Parse(@"<param len=""COMPSIZE(format, type, width)"">const void *<name>table</name></param>");
            var param = GlParameter.Parse(node);
            Assert.That(param.Group, Is.Null);
            Assert.That(param.Type.BaseType, Is.EqualTo(GlBaseType.Void));
            Assert.That(param.Type.Modifier, Is.EqualTo(GlTypeModifier.PointerToConst));
            Assert.That(param.Name, Is.EqualTo("table"));
        }

        [Test]
        public void CanParseParameterDeclarationWithPtypeNodeAndExtraText()
        {
            var node = XElement.Parse(@"<param len=""count"">const <ptype>GLchar</ptype> *const*<name>path</name></param>");
            var param = GlParameter.Parse(node);
            Assert.That(param.Group, Is.Null);
            Assert.That(param.Type.BaseType, Is.EqualTo(GlBaseType.Char));
            Assert.That(param.Type.Modifier, Is.EqualTo(GlTypeModifier.PointerToConstPointerToConst));
            Assert.That(param.Name, Is.EqualTo("path"));
        }
    }
}