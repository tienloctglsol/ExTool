using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace ExTool
{
    internal static class BlockBuilder
    {
        // Clone ids (cùng nằm trong current space) vào block mới, thay bản gốc bằng block reference đặt tại basePoint
        public static ObjectId Create(Transaction tr, Database db, ObjectIdCollection ids, string name, Point3d basePoint, ObjectId layerId)
        {
            var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForWrite);
            var btr = new BlockTableRecord { Name = name, Origin = Point3d.Origin };
            ObjectId btrId = blockTable.Add(btr);
            tr.AddNewlyCreatedDBObject(btr, true);

            // Dời về gốc để base point của block trùng basePoint
            var mapping = new IdMapping();
            db.DeepCloneObjects(ids, btrId, mapping, false);
            Matrix3d toOrigin = Matrix3d.Displacement(Point3d.Origin - basePoint);
            foreach (IdPair pair in mapping)
            {
                if (!pair.IsPrimary || !pair.IsCloned) continue;
                var clone = (Entity)tr.GetObject(pair.Value, OpenMode.ForWrite);
                clone.TransformBy(toOrigin);
            }

            var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
            var blockRef = new BlockReference(basePoint, btrId);
            blockRef.SetDatabaseDefaults(db);
            if (!layerId.IsNull) blockRef.LayerId = layerId;
            space.AppendEntity(blockRef);
            tr.AddNewlyCreatedDBObject(blockRef, true);
            AddAttributes(tr, btr, blockRef);

            foreach (ObjectId id in ids)
                tr.GetObject(id, OpenMode.ForWrite).Erase();

            return blockRef.ObjectId;
        }

        public static string GetUniqueName(BlockTable blockTable, string prefix)
        {
            int i = 1;
            while (blockTable.Has(prefix + i)) i++;
            return prefix + i;
        }

        private static void AddAttributes(Transaction tr, BlockTableRecord btr, BlockReference blockRef)
        {
            if (!btr.HasAttributeDefinitions) return;

            var attDefs = new List<AttributeDefinition>();
            foreach (ObjectId id in btr)
            {
                if (tr.GetObject(id, OpenMode.ForRead) is AttributeDefinition attDef && !attDef.Constant)
                    attDefs.Add(attDef);
            }

            foreach (AttributeDefinition attDef in attDefs)
            {
                var attRef = new AttributeReference();
                attRef.SetAttributeFromBlock(attDef, blockRef.BlockTransform);
                attRef.TextString = attDef.TextString;
                blockRef.AttributeCollection.AppendAttribute(attRef);
                tr.AddNewlyCreatedDBObject(attRef, true);
            }
        }
    }
}
