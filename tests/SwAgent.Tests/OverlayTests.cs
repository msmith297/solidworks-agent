using SwAgent.UI;
using Xunit;

namespace SwAgent.Tests
{
    public class OverlayTests
    {
        [Fact]
        public void A_tag_edit_keeps_the_value_and_note_and_shows_what_it_replaced()
        {
            var item = new SceneOverlay.Item { Id = 7, Type = "dimension", Text = "5.04" };
            var edit = SceneOverlay.ApplyEdit(item, " 5.43 ", " to the tube ");
            Assert.Equal(7, edit.Id);
            Assert.Equal("5.04", edit.Original);
            Assert.Equal("5.43", edit.Value);
            Assert.Equal("to the tube", edit.Note);
            Assert.Equal("5.43  (was 5.04)\nto the tube", SceneOverlay.TagText(item));
        }

        [Theory]
        [InlineData("5.04")]
        [InlineData("")]
        [InlineData("   ")]
        public void A_tag_saved_with_its_own_value_or_none_is_no_change(string value)
        {
            var item = new SceneOverlay.Item { Type = "dimension", Text = "5.04", EditedText = "9", Note = "old" };
            var edit = SceneOverlay.ApplyEdit(item, value, "");
            Assert.Null(edit.Value);
            Assert.Null(edit.Note);
            Assert.Equal("5.04", SceneOverlay.TagText(item));
        }

        [Fact]
        public void A_note_alone_leaves_the_value_as_drawn()
        {
            var item = new SceneOverlay.Item { Type = "label", Text = "neck" };
            var edit = SceneOverlay.ApplyEdit(item, "neck", "same on all rails");
            Assert.Null(edit.Value);
            Assert.Equal("neck\nsame on all rails", SceneOverlay.TagText(item));
        }

        [Fact]
        public void The_mouse_finds_the_tag_on_top_and_nothing_outside_the_tags()
        {
            var below = new SceneOverlay.Item();
            var above = new SceneOverlay.Item();
            var tags = new System.Collections.Generic.List<(System.Drawing.RectangleF, SceneOverlay.Item)>
            {
                (new System.Drawing.RectangleF(0, 0, 50, 20), below),
                (new System.Drawing.RectangleF(40, 0, 50, 20), above),
            };
            Assert.Same(below, SceneOverlay.TagAt(tags, new System.Drawing.PointF(10, 10)));
            Assert.Same(above, SceneOverlay.TagAt(tags, new System.Drawing.PointF(45, 10)));
            Assert.Null(SceneOverlay.TagAt(tags, new System.Drawing.PointF(200, 10)));
        }

        /// <summary>
        /// IModelView.Transform and the pixel positions SolidWorks itself returned for it (MathPoint.MultiplyTransform),
        /// read from a live session: the overlay's projection must land on the same pixels.
        /// </summary>
        [Theory]
        [InlineData(-0.1651, 0.0378, 0.0, 2407.5204, 523.1242)]
        [InlineData(-0.1727, 0.0371, -0.0065, 2465.5141, 523.1242)]
        [InlineData(-0.1691, 0.0462, 0.0037, 2407.5204, 465.1306)]
        public void Projection_matches_solidworks(double x, double y, double z, double px, double py)
        {
            var t = new[] { -0.7583, 0.4036, -0.512, -0.078, -0.8359, -0.5432, -0.6473, -0.372, 0.6654, 1698.6404, 1092.9793, -1724.1099, 5799.3631, 0, 0, 0 };
            var p = SceneOverlay.Project(t, new[] { x, y, z });
            Assert.InRange(p.X, px - 3, px + 3); // the probe printed the transform rounded to 4 decimals
            Assert.InRange(p.Y, py - 3, py + 3);
        }

        /// <summary>A plate at z = 10 mm in front of the camera: what is behind it is hidden; on it, in front of it or beside it is not.</summary>
        [Fact]
        public void Depth_test_hides_only_what_the_model_covers()
        {
            // View looking down -Z (toward the viewer = +Z), 1000 px per meter, origin at pixel (500, 500).
            var t = new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, 500, 500, 0, 1000, 0, 0, 0 };
            const float z = 0.010f;
            var plate = new float[] { -0.1f, -0.1f, z, 0.1f, -0.1f, z, 0.1f, 0.1f, z, -0.1f, -0.1f, z, 0.1f, 0.1f, z, -0.1f, 0.1f, z };
            var item = new SceneOverlay.Item { Type = "line" };
            var points = new System.Collections.Generic.List<double[]>
            {
                new double[] { 0, 0, 0 },          // behind the plate
                new double[] { 0, 0, 0.02 },       // in front of it
                new double[] { 0.05, 0.05, 0.010 }, // on its surface
                new double[] { 0.3, 0, 0 },        // beside it
            };
            var hidden = SceneOverlay.ComputeHidden(plate, t, 1, 1000, 1000, new System.Collections.Generic.List<(SceneOverlay.Item, System.Collections.Generic.List<double[]>)> { (item, points) })[item];
            Assert.Equal(new[] { true, false, false, false }, hidden);
        }
    }
}
