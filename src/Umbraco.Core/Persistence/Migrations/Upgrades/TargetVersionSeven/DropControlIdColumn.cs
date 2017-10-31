﻿using System;
using Umbraco.Core.Configuration;
using Umbraco.Core.Persistence.SqlSyntax;

namespace Umbraco.Core.Persistence.Migrations.Upgrades.TargetVersionSeven
{
    [Migration("7.0.0", 2, Constants.System.UmbracoMigrationName)]
    public class DropControlIdColumn : MigrationBase
    {
        public DropControlIdColumn(IMigrationContext context)
            : base(context)
        { }


        public override void Up()
        {
            Delete.Column("controlId").FromTable(Constants.DatabaseSchema.Tables.DataType);
            //drop the default contstraint on the new column too
            Delete.DefaultConstraint().OnTable(Constants.DatabaseSchema.Tables.DataType).OnColumn("propertyEditorAlias");
        }

        public override void Down()
        {
            throw new DataLossException("Cannot downgrade from a version 7 database to a prior version, the database schema has already been modified");
        }
    }
}
