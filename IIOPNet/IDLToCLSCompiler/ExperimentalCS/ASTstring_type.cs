/* Generated By:JJTree: Do not edit this line. ASTstring_type.cs */

using System;

namespace parser {

public class ASTstring_type : SimpleNode {
  public ASTstring_type(int id) : base(id) {
  }

  public ASTstring_type(IDLParser p, int id) : base(p, id) {
  }


  /** Accept the visitor. **/
  public override Object jjtAccept(IDLParserVisitor visitor, Object data) {
    return visitor.visit(this, data);
  }
}


}

